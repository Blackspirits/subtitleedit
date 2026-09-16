using Nikse.SubtitleEdit.Logic.Config;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Nikse.SubtitleEdit.Logic.VideoPlayers.Ffmpeg.Audio;

internal static class WaveOutPosition
{
    internal const uint TimeMilliseconds = 0x0001;
    internal const uint TimeSamples = 0x0002;
    internal const uint TimeBytes = 0x0004;

    internal static long? CounterToBytes(uint type, uint value, int blockAlign, int bytesPerSecond)
    {
        return type switch
        {
            TimeBytes => value,
            TimeSamples => (long)value * blockAlign,
            TimeMilliseconds => (long)value * bytesPerSecond / 1000,
            _ => null,
        };
    }

    internal static long CounterWrapBytes(uint type, int blockAlign, int bytesPerSecond)
    {
        const long counterSpan = 1L << 32;
        return type switch
        {
            TimeBytes => counterSpan,
            TimeSamples => counterSpan * blockAlign,
            TimeMilliseconds => counterSpan * bytesPerSecond / 1000,
            _ => 0,
        };
    }

    internal static long WrapBaseAfterFormatChange(long convertedBytes, long lastPositionBytes, long wrapBytes)
    {
        if (wrapBytes <= 0 || convertedBytes >= lastPositionBytes)
        {
            return 0;
        }

        // Pick the nearest wrap epoch. A driver switching from samples to milliseconds can round
        // the same instant a few bytes backwards; that is not evidence of a 32-bit counter wrap.
        var difference = lastPositionBytes - convertedBytes;
        return ((difference + wrapBytes / 2) / wrapBytes) * wrapBytes;
    }
}

/// <summary>
/// Windows audio output through the classic waveOut API (winmm.dll). It is available on every
/// Windows, needs no COM apartment, and reports the played position straight from the driver -
/// which is what makes it a good master clock. A fixed ring of small buffers is queued to the
/// device; <see cref="Write"/> blocks while all of them are in flight.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class WaveOutAudioSink : IAudioSink
{
    private const int BufferCount = 12;
    private const int BufferMilliseconds = 20;

    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint CallbackEvent = 0x00050000;
    private const uint WhdrDone = 0x00000001;
    private const uint MmSysErrNoError = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MmTime
    {
        public uint wType;
        public uint u; // cb / sample / ms depending on wType
        public uint pad; // the union is 8 bytes (smpte)
    }

    [LibraryImport("winmm.dll")]
    private static partial uint waveOutOpen(out IntPtr hWaveOut, uint uDeviceId, ref WaveFormatEx lpFormat, IntPtr dwCallback, IntPtr dwInstance, uint dwFlags);

    [LibraryImport("winmm.dll")]
    private static partial uint waveOutClose(IntPtr hWaveOut);

    [LibraryImport("winmm.dll")]
    private static partial uint waveOutPrepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, uint uSize);

    [LibraryImport("winmm.dll")]
    private static partial uint waveOutUnprepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, uint uSize);

    [LibraryImport("winmm.dll")]
    private static partial uint waveOutWrite(IntPtr hWaveOut, IntPtr lpWaveOutHdr, uint uSize);

    [LibraryImport("winmm.dll")]
    private static partial uint waveOutPause(IntPtr hWaveOut);

    [LibraryImport("winmm.dll")]
    private static partial uint waveOutRestart(IntPtr hWaveOut);

    [LibraryImport("winmm.dll")]
    private static partial uint waveOutReset(IntPtr hWaveOut);

    [LibraryImport("winmm.dll")]
    private static partial uint waveOutGetPosition(IntPtr hWaveOut, ref MmTime pmmt, uint cbmmt);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateEventW(IntPtr lpEventAttributes, [MarshalAs(UnmanagedType.Bool)] bool bManualReset, [MarshalAs(UnmanagedType.Bool)] bool bInitialState, IntPtr lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    private readonly Lock _lock = new();
    private IntPtr _device = IntPtr.Zero;
    private IntPtr _doneEvent = IntPtr.Zero;
    private IntPtr _headers = IntPtr.Zero; // BufferCount x WaveHdr
    private IntPtr _data = IntPtr.Zero; // BufferCount x _bufferBytes
    private int _bufferBytes;
    private int _bytesPerSecond;
    private int _blockAlign = 4;
    private int _nextBuffer;
    private int _generation;
    private int _serial;
    private volatile bool _disposed;
    private volatile bool _paused;

    // Audio that has left the device before the last Reset. waveOutReset does not rewind the
    // driver's position counter on every driver, so the played time is measured relative to
    // the counter value seen at the reset.
    private long _positionBase;
    private long _lastRawPosition;
    private uint _lastPositionType;
    private uint _lastPositionCounter;
    private long _positionWrapBaseBytes;

    public void Open(int sampleRate, int channels)
    {
        lock (_lock)
        {
            if (!CloseCore())
            {
                throw new InvalidOperationException("Previous waveOut device could not be closed safely");
            }

            var format = new WaveFormatEx
            {
                wFormatTag = 1, // WAVE_FORMAT_PCM
                nChannels = (ushort)channels,
                nSamplesPerSec = (uint)sampleRate,
                wBitsPerSample = 16,
                nBlockAlign = (ushort)(channels * 2),
                nAvgBytesPerSec = (uint)(sampleRate * channels * 2),
                cbSize = 0,
            };
            _bytesPerSecond = (int)format.nAvgBytesPerSec;
            _blockAlign = format.nBlockAlign;
            _bufferBytes = _bytesPerSecond * BufferMilliseconds / 1000;
            _bufferBytes -= _bufferBytes % format.nBlockAlign;

            _doneEvent = CreateEventW(IntPtr.Zero, false, false, IntPtr.Zero);
            if (_doneEvent == IntPtr.Zero)
            {
                throw new InvalidOperationException($"CreateEventW failed with error {Marshal.GetLastWin32Error()}");
            }

            var result = waveOutOpen(out _device, WaveMapper, ref format, _doneEvent, IntPtr.Zero, CallbackEvent);
            if (result != MmSysErrNoError)
            {
                CloseCore();
                throw new InvalidOperationException($"waveOutOpen failed with error {result}");
            }

            _headers = Marshal.AllocHGlobal(sizeof(WaveHdr) * BufferCount);
            _data = Marshal.AllocHGlobal(_bufferBytes * BufferCount);

            // Initialize every header before preparing any of them. If preparation later fails,
            // CloseCore can safely unprepare the whole array instead of touching uninitialized
            // native memory after the failing index.
            for (var i = 0; i < BufferCount; i++)
            {
                var header = (WaveHdr*)_headers + i;
                *header = new WaveHdr
                {
                    lpData = _data + i * _bufferBytes,
                    dwBufferLength = (uint)_bufferBytes,
                    dwFlags = 0, // must be zero when prepared
                };
            }

            for (var i = 0; i < BufferCount; i++)
            {
                var header = (WaveHdr*)_headers + i;
                result = waveOutPrepareHeader(_device, (IntPtr)header, (uint)sizeof(WaveHdr));
                if (result != MmSysErrNoError)
                {
                    CloseCore();
                    throw new InvalidOperationException($"waveOutPrepareHeader failed with error {result}");
                }

                header->dwFlags |= WhdrDone; // free
            }

            _nextBuffer = 0;
            _positionBase = 0;
            _lastRawPosition = 0;
            _lastPositionType = 0;
            _lastPositionCounter = 0;
            _positionWrapBaseBytes = 0;
            _serial = 0;
            _paused = false;
        }
    }

    public double PlayedSeconds
    {
        get
        {
            TryGetPlayedSeconds(out var playedSeconds);
            return playedSeconds;
        }
    }

    public bool TryGetPlayedSeconds(out double playedSeconds)
    {
        lock (_lock)
        {
            if (_device == IntPtr.Zero || _bytesPerSecond == 0)
            {
                playedSeconds = 0;
                return false;
            }

            var valid = TryGetRawPositionBytes(out var raw);
            playedSeconds = Math.Max(0, raw - _positionBase) / (double)_bytesPerSecond;
            return valid;
        }
    }

    private bool TryGetRawPositionBytes(out long position)
    {
        position = _lastRawPosition;
        // Samples are Microsoft's preferred waveform position format. Drivers may still answer
        // in another supported MMTIME format, so normalize the returned type rather than assuming
        // the request was honoured.
        var time = new MmTime { wType = WaveOutPosition.TimeSamples };
        if (waveOutGetPosition(_device, ref time, (uint)sizeof(MmTime)) != MmSysErrNoError)
        {
            return false;
        }

        var converted = WaveOutPosition.CounterToBytes(time.wType, time.u, _blockAlign, _bytesPerSecond);
        if (!converted.HasValue)
        {
            return false;
        }

        var wrapBytes = WaveOutPosition.CounterWrapBytes(time.wType, _blockAlign, _bytesPerSecond);
        if (_lastPositionType == time.wType)
        {
            if (time.u < _lastPositionCounter && wrapBytes > 0)
            {
                _positionWrapBaseBytes += wrapBytes;
            }
        }
        else
        {
            // A driver is allowed to answer a later query in a different format. Choose the wrap
            // epoch nearest the previous absolute position; small conversion-rounding differences
            // must not be mistaken for an entire 32-bit counter wrap.
            _positionWrapBaseBytes = WaveOutPosition.WrapBaseAfterFormatChange(
                converted.Value,
                _lastRawPosition,
                wrapBytes);
        }

        var absolutePosition = _positionWrapBaseBytes + converted.Value;
        if (absolutePosition < _lastRawPosition)
        {
            // Millisecond conversion can round a format switch slightly backwards.
            absolutePosition = _lastRawPosition;
        }

        _lastPositionType = time.wType;
        _lastPositionCounter = time.u;
        _lastRawPosition = absolutePosition;
        position = absolutePosition;
        return true;
    }

    public bool Write(ReadOnlySpan<byte> pcm, int serial)
    {
        var generation = Volatile.Read(ref _generation);
        var offset = 0;
        while (offset < pcm.Length)
        {
            var queued = false;
            lock (_lock)
            {
                if (_disposed || _device == IntPtr.Zero || generation != _generation || serial != _serial)
                {
                    return false;
                }

                var header = (WaveHdr*)_headers + _nextBuffer;
                if ((header->dwFlags & WhdrDone) != 0)
                {
                    var count = Math.Min(_bufferBytes, pcm.Length - offset);
                    pcm.Slice(offset, count).CopyTo(new Span<byte>((void*)header->lpData, count));

                    header->dwBufferLength = (uint)count;
                    header->dwFlags &= ~WhdrDone;
                    if (waveOutWrite(_device, (IntPtr)header, (uint)sizeof(WaveHdr)) != MmSysErrNoError)
                    {
                        header->dwFlags |= WhdrDone;
                        return false;
                    }

                    _nextBuffer = (_nextBuffer + 1) % BufferCount;
                    offset += count;
                    queued = true;
                }
            }

            if (!queued)
            {
                // All buffers are queued - wait for the driver to hand one back. Copying into a
                // buffer and enqueuing it stay under _lock so Reset cannot recycle that buffer
                // between the copy and the final serial/generation check.
                WaitForSingleObject(_doneEvent, (uint)BufferMilliseconds);
            }
        }

        return true;
    }

    public void Reset(int serial)
    {
        lock (_lock)
        {
            Interlocked.Increment(ref _generation);
            Volatile.Write(ref _serial, AudioSinkResetFence.RejectedSerial);
            if (_device == IntPtr.Zero)
            {
                return;
            }

            var resetResult = waveOutReset(_device);
            if (resetResult != MmSysErrNoError)
            {
                // Only a successful reset guarantees that queued headers were returned. Keep the
                // serial fenced and leave header ownership untouched rather than recycling memory
                // the driver may still be using.
                Se.LogError($"ffmpeg player: waveOutReset failed with error {resetResult}; audio writes remain fenced");
                return;
            }

            // Drivers differ on whether waveOutReset rewinds the position counter; forget the
            // wrap-around history first so a rewind to 0 is not mistaken for a 32-bit wrap.
            _lastRawPosition = 0;
            _lastPositionType = 0;
            _lastPositionCounter = 0;
            _positionWrapBaseBytes = 0;
            TryGetRawPositionBytes(out var positionBase);
            _positionBase = positionBase;
            _nextBuffer = 0;
            for (var i = 0; i < BufferCount; i++)
            {
                ((WaveHdr*)_headers + i)->dwFlags |= WhdrDone;
            }

            Volatile.Write(ref _serial, AudioSinkResetFence.SerialAfterReset(serial, succeeded: true));
            if (_paused)
            {
                waveOutPause(_device); // waveOutReset implicitly restarts a paused device
            }
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            _paused = true;
            if (_device != IntPtr.Zero)
            {
                waveOutPause(_device);
            }
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            _paused = false;
            if (_device != IntPtr.Zero)
            {
                waveOutRestart(_device);
            }
        }
    }

    /// <summary>
    /// Releases native resources only after WinMM confirms it no longer owns any waveform
    /// buffers. A failure keeps the complete device/buffer/event boundary alive so Dispose can
    /// be retried instead of freeing memory that the driver may still reference.
    /// Called under <see cref="_lock"/>.
    /// </summary>
    private bool CloseCore()
    {
        if (_device != IntPtr.Zero)
        {
            var resetResult = waveOutReset(_device);
            if (resetResult != MmSysErrNoError)
            {
                Se.LogError($"ffmpeg player: waveOutReset failed during teardown with error {resetResult}; retaining WinMM resources");
                return false;
            }

            if (_headers != IntPtr.Zero)
            {
                for (var i = 0; i < BufferCount; i++)
                {
                    var unprepareResult = waveOutUnprepareHeader(_device, (IntPtr)((WaveHdr*)_headers + i), (uint)sizeof(WaveHdr));
                    if (unprepareResult != MmSysErrNoError)
                    {
                        Se.LogError($"ffmpeg player: waveOutUnprepareHeader failed during teardown with error {unprepareResult}; retaining WinMM resources");
                        return false;
                    }
                }
            }

            var closeResult = waveOutClose(_device);
            if (closeResult != MmSysErrNoError)
            {
                Se.LogError($"ffmpeg player: waveOutClose failed during teardown with error {closeResult}; retaining WinMM resources");
                return false;
            }

            _device = IntPtr.Zero;
        }

        if (_headers != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_headers);
            _headers = IntPtr.Zero;
        }

        if (_data != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_data);
            _data = IntPtr.Zero;
        }

        if (_doneEvent != IntPtr.Zero)
        {
            if (!CloseHandle(_doneEvent))
            {
                Se.LogError($"ffmpeg player: CloseHandle failed during waveOut teardown with error {Marshal.GetLastWin32Error()}; retaining event handle");
                return false;
            }

            _doneEvent = IntPtr.Zero;
        }

        return true;
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_lock)
        {
            Interlocked.Increment(ref _generation);
            CloseCore();
        }
    }
}
