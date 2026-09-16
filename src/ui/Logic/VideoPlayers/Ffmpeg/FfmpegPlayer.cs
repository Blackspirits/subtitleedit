using FFmpeg.AutoGen;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.Logic.VideoPlayers.Ffmpeg.Audio;
using Nikse.SubtitleEdit.Logic.VideoPlayers.LibMpvDynamic;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Logic.VideoPlayers.Ffmpeg;

/// <summary>
/// Video player built directly on the FFmpeg libraries (libavformat/libavcodec/libswscale/
/// libswresample through FFmpeg.AutoGen), in the spirit of ffme / ffmediaelement and ffplay:
/// <list type="bullet">
/// <item>a demux thread reads packets into one <see cref="PacketQueue"/> per stream,</item>
/// <item>a video thread decodes and converts pictures to BGRA into a <see cref="VideoFrameQueue"/>,</item>
/// <item>an audio thread decodes, resamples to 16-bit stereo and feeds an <see cref="IAudioSink"/>,</item>
/// <item>a presenter thread shows each picture when the clock reaches its time stamp.</item>
/// </list>
/// The clock is the audio device (<see cref="IAudioSink.PlayedSeconds"/>) when the file has audio
/// and a stopwatch otherwise. Seeks are numbered ("serials", as in ffplay): the demux thread
/// flushes the queues and bumps the serial, and every consumer discards what belongs to an older
/// serial, so a seek never has to wait for the pipeline to drain.
/// <para>
/// Pictures are software decoded and handed to an Avalonia control through
/// <see cref="CopyCurrentFrame"/>; see <see cref="FfmpegSoftwareControl"/>.
/// </para>
/// </summary>
public sealed unsafe class FfmpegPlayer : IVideoPlayer, IDisposable
{
    public string PlayerSubName { get; set; } = string.Empty;

    private const int OutputSampleRate = 48000;
    private const int OutputChannels = 2;
    private const double OutputBytesPerSecond = OutputSampleRate * OutputChannels * 2.0;
    private const int VideoQueueCapacity = 3;

    /// <summary>Pictures wider than this are scaled down during conversion - the preview never shows more, and it keeps the BGRA pool small.</summary>
    private const int MaxOutputWidth = 1920;

    private const double MaxDemuxQueueBytes = 24 * 1024 * 1024;
    private const int MaxDemuxQueuePackets = 200;

    private string _fileName = string.Empty;
    private bool _disposed;
    private Session? _session;
    private int _loadGeneration; // bumped by LoadFile/CloseFile so a stale open cannot publish its session
    private double _volume = 100;
    private double _speed = 1.0;

    // Frame handed to the UI. Guarded by _currentFrameLock; the presenter swaps it, the control copies it.
    private readonly Lock _currentFrameLock = new();
    private VideoFrame? _currentFrame;
    private long _frameVersion;

    /// <summary>Raised (on a worker thread) whenever a new picture is ready for <see cref="CopyCurrentFrame"/>.</summary>
    public event Action? FrameReady;

    /// <summary>
    /// The subtitle drawn over the video by <see cref="FfmpegSoftwareControl"/>. mpv and VLC get
    /// an ASS file pushed into the player; this player has no renderer, so the control draws
    /// this snapshot itself. Replace it whenever the subtitle changes (see MainViewModel).
    /// </summary>
    public FfmpegPreviewSubtitle PreviewSubtitle
    {
        get => _previewSubtitle;
        set => _previewSubtitle = value ?? FfmpegPreviewSubtitle.Empty;
    }

    private volatile FfmpegPreviewSubtitle _previewSubtitle = FfmpegPreviewSubtitle.Empty;

    /// <summary>The "toggle subtitles on video player" state, shared with mpv's flag by the caller.</summary>
    public volatile bool PreviewSubtitlesVisible = true;

    /// <summary>
    /// Hardware decoder in use ("videotoolbox", "d3d11va", ...), or empty while decoding in
    /// software. Written by the video thread, read by the UI badge.
    /// </summary>
    private volatile string _decoderName = string.Empty;

    public string Name => string.IsNullOrEmpty(_decoderName) ? "ffmpeg" : $"ffmpeg ({_decoderName})";
    public string FileName => _fileName;

    public bool CanLoad()
    {
        return FfmpegLibraries.IsAvailable();
    }

    public int VideoWidth => _session?.VideoWidth ?? 0;
    public int VideoHeight => _session?.VideoHeight ?? 0;

    /// <summary>Display aspect ratio of the video (sample aspect ratio applied), or 0 when unknown.</summary>
    public double DisplayAspectRatio => _session?.DisplayAspectRatio ?? 0;

    /// <summary>Incremented on every presented picture, so a control can skip redundant copies.</summary>
    public long FrameVersion => Interlocked.Read(ref _frameVersion);

    /// <summary>
    /// Copies the current picture as BGRA into <paramref name="destination"/>. Returns false (and
    /// leaves the destination alone) when there is no picture or the sizes do not match.
    /// </summary>
    public bool CopyCurrentFrame(IntPtr destination, int destinationStride, int width, int height)
    {
        lock (_currentFrameLock)
        {
            var frame = _currentFrame;
            if (frame == null || frame.Data == IntPtr.Zero || frame.Width != width || frame.Height != height)
            {
                return false;
            }

            var rowBytes = frame.Width * 4;
            var source = (byte*)frame.Data;
            var target = (byte*)destination;
            for (var y = 0; y < height; y++)
            {
                Buffer.MemoryCopy(source + (long)y * frame.Stride, target + (long)y * destinationStride, rowBytes, rowBytes);
            }

            return true;
        }
    }

    /// <summary>Size of the picture currently held for the UI (may be smaller than the video, see <see cref="MaxOutputWidth"/>).</summary>
    public (int Width, int Height) CurrentFrameSize
    {
        get
        {
            lock (_currentFrameLock)
            {
                return _currentFrame == null ? (0, 0) : (_currentFrame.Width, _currentFrame.Height);
            }
        }
    }

    public Task LoadFile(string fileName, double startPositionSeconds = 0)
    {
        CloseFile();
        _fileName = fileName;

        // Opening runs on a worker; a CloseFile or another LoadFile issued meanwhile bumps the
        // generation, and the worker then throws its session away instead of resurrecting it.
        var generation = Interlocked.Increment(ref _loadGeneration);

        return Task.Run(() =>
        {
            if (_disposed || generation != Volatile.Read(ref _loadGeneration))
            {
                return;
            }

            Session session;
            try
            {
                session = new Session(this, fileName);
            }
            catch (Exception exception)
            {
                Se.LogError(exception, $"ffmpeg player failed to open: {fileName}");
                if (generation == Volatile.Read(ref _loadGeneration))
                {
                    _fileName = string.Empty;
                }

                return;
            }

            if (_disposed || generation != Volatile.Read(ref _loadGeneration) ||
                Interlocked.CompareExchange(ref _session, session, null) != null)
            {
                session.Dispose();
                return;
            }

            if (generation != Volatile.Read(ref _loadGeneration))
            {
                // CloseFile ran between the check and the exchange; whoever still holds the
                // session disposes it.
                if (Interlocked.CompareExchange(ref _session, null, session) == session)
                {
                    session.Dispose();
                }

                return;
            }

            try
            {
                session.Volume = _volume;
                session.Speed = _speed;
                session.Start();

                // Always seek once: this is what decodes and shows the first picture (at the wanted
                // position) while the player stays paused.
                session.Seek(Math.Max(0, startPositionSeconds));
            }
            catch (ObjectDisposedException)
            {
                // CloseFile took and disposed the session while it was being started.
            }
        });
    }

    public void CloseFile()
    {
        Interlocked.Increment(ref _loadGeneration);
        var session = Interlocked.Exchange(ref _session, null);
        _fileName = string.Empty;
        session?.Dispose();

        lock (_currentFrameLock)
        {
            // The frame belonged to the session's pool, which is gone now.
            _currentFrame?.Dispose();
            _currentFrame = null;
        }

        Interlocked.Increment(ref _frameVersion);
        FrameReady?.Invoke();
    }

    public void Play()
    {
        _session?.Play();
    }

    public void PlayOrPause()
    {
        var session = _session;
        if (session == null)
        {
            return;
        }

        if (session.IsPlaying)
        {
            session.Pause();
        }
        else
        {
            session.Play();
        }
    }

    public void Pause()
    {
        _session?.Pause();
    }

    public void Stop()
    {
        var session = _session;
        if (session == null)
        {
            return;
        }

        session.Pause();
        session.Seek(0);
    }

    public AudioTrackInfo? ToggleAudioTrack()
    {
        return _session?.ToggleAudioTrack();
    }

    public bool IsPlaying => _session?.IsPlaying ?? false;
    public bool IsPaused => !IsPlaying;

    public double Position
    {
        get => _session?.Position ?? 0;
        set
        {
            var session = _session;
            if (session == null)
            {
                return;
            }

            var target = SeekTarget(value, session.Duration);
            if (target.HasValue)
            {
                session.Seek(target.Value);
            }
        }
    }

    /// <summary>
    /// Where a Position assignment seeks to: null for an invalid value, else the value clamped
    /// to the duration - but only when the duration is known. Raw elementary streams (.h264) and
    /// some transport streams report no duration (0 or NaN), and clamping to that would turn
    /// every seek into a seek to 0.
    /// </summary>
    internal static double? SeekTarget(double value, double duration)
    {
        if (value < 0 || double.IsNaN(value))
        {
            return null;
        }

        return duration > 0 ? Math.Min(value, duration) : value;
    }

    internal static double EndPosition(double duration, double observedPosition)
    {
        if (double.IsFinite(duration) && duration > 0)
        {
            return duration;
        }

        return double.IsFinite(observedPosition) && observedPosition > 0 ? observedPosition : 0;
    }

    internal static bool AudioDrainComplete(int eofSerial, int currentSerial, double audioEndPosition, double clock)
    {
        if (eofSerial != currentSerial)
        {
            return false;
        }

        return !double.IsFinite(audioEndPosition) || clock >= audioEndPosition - 0.005;
    }

    /// <summary>
    /// State visible after a native seek failure. Only the request that actually failed may roll
    /// back to the committed pipeline serial/position; a newer request that arrived while
    /// av_seek_frame was blocked must remain pending.
    /// </summary>
    internal static (int RequestedSerial, double RequestedTarget) FailedSeekState(
        int failedSerial,
        int currentSerial,
        int requestedSerial,
        double requestedTarget,
        double currentPosition)
    {
        return requestedSerial == failedSerial
            ? (currentSerial, currentPosition)
            : (requestedSerial, requestedTarget);
    }

    internal static int FailedSeekAudioStreamState(int failedSerial, int requestedSerial, int requestedAudioStreamIndex)
    {
        return requestedSerial == failedSerial ? -1 : requestedAudioStreamIndex;
    }

    internal static int NextAudioStreamIndex(IReadOnlyList<int> streamIndexes, int committedAudioStreamIndex, int requestedAudioStreamIndex)
    {
        if (streamIndexes.Count == 0)
        {
            return -1;
        }

        var selected = requestedAudioStreamIndex >= 0 ? requestedAudioStreamIndex : committedAudioStreamIndex;
        var current = -1;
        for (var i = 0; i < streamIndexes.Count; i++)
        {
            if (streamIndexes[i] == selected)
            {
                current = i;
                break;
            }
        }

        return streamIndexes[(current + 1) % streamIndexes.Count];
    }

    internal static bool ShouldAutoRewindOnPlay(
        bool hasOutstandingSeek,
        bool endReached,
        double duration,
        double position)
    {
        return !hasOutstandingSeek &&
               (endReached || (duration > 0 && position >= duration - 0.01));
    }

    internal static bool ShouldReachAudioOnlyEnd(
        bool playing,
        bool hasOutstandingSeek,
        double duration,
        double position)
    {
        return playing &&
               !hasOutstandingSeek &&
               duration > 0 &&
               position >= duration;
    }

    internal static double WallClockPosition(double basePosition, double elapsedSeconds, double speed)
    {
        return basePosition + elapsedSeconds * speed;
    }

    public double Duration => _session?.Duration ?? 0;

    public int VolumeMaximum => 100;

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, VolumeMaximum);
            var session = _session;
            if (session != null)
            {
                session.Volume = _volume;
            }
        }
    }

    public double Speed
    {
        get => _speed;
        set
        {
            if (value <= 0 || double.IsNaN(value))
            {
                return;
            }

            _speed = value;
            var session = _session;
            if (session != null)
            {
                session.Speed = value;
            }
        }
    }

    public bool SupportsPlaybackRestartEvents => true;

    public bool HasPlaybackRestartedSince(long stopwatchTimestamp)
    {
        return _session?.HasPlaybackRestartedSince(stopwatchTimestamp) ?? false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseFile();
    }

    private void Present(VideoFrame frame, VideoFrameQueue pool)
    {
        VideoFrame? previous;
        lock (_currentFrameLock)
        {
            previous = _currentFrame;
            _currentFrame = frame;
        }

        pool.Return(previous);
        Interlocked.Increment(ref _frameVersion);
        FrameReady?.Invoke();
    }

    private static IAudioSink CreateAudioSink()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WaveOutAudioSink();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new AudioQueueAudioSink();
        }

        // No sink for Linux yet - playback stays in sync, just silent.
        return new SilentAudioSink();
    }

    /// <summary>
    /// Whether samples of this format are stored one plane per channel. The same table as
    /// libavutil's av_sample_fmt_is_planar, kept managed so it can be tested without the native
    /// libraries. Note that the enum is not ordered packed-then-planar: S64 (packed) comes after
    /// the first planar formats, so a ">= U8P" comparison misclassifies it.
    /// </summary>
    internal static bool IsPlanarSampleFormat(AVSampleFormat format)
    {
        return format is AVSampleFormat.AV_SAMPLE_FMT_U8P
            or AVSampleFormat.AV_SAMPLE_FMT_S16P
            or AVSampleFormat.AV_SAMPLE_FMT_S32P
            or AVSampleFormat.AV_SAMPLE_FMT_FLTP
            or AVSampleFormat.AV_SAMPLE_FMT_DBLP
            or AVSampleFormat.AV_SAMPLE_FMT_S64P;
    }

    internal static bool AudioWriteCanAnchor(bool writeAccepted, int serial, int currentSerial)
    {
        return writeAccepted && serial == currentSerial;
    }

    /// <summary>
    /// avcodec_send_packet(EAGAIN) means the decoder rejected the input. The same packet may be
    /// resent only after receive_frame made progress, and only while the serial is still current.
    /// </summary>
    internal static bool ShouldResendPacket(int sendResult, bool receivedOutput, bool interrupted)
    {
        return sendResult == -ffmpeg.EAGAIN && receivedOutput && !interrupted;
    }

    internal static bool AudioWriteFailureIsDeviceFailure(
        bool writeAccepted,
        bool closing,
        int serial,
        int currentSerial,
        int requestedSerial)
    {
        return !writeAccepted &&
               !closing &&
               serial == currentSerial &&
               serial == requestedSerial;
    }

    internal static double AudioClockFailoverPosition(
        double audioAnchorPts,
        int audioAnchorSerial,
        int serial,
        double playedSeconds,
        double audioSpeed,
        double wallClockPosition)
    {
        return !double.IsNaN(audioAnchorPts) && audioAnchorSerial == serial
            ? audioAnchorPts + playedSeconds * audioSpeed
            : wallClockPosition;
    }

    internal static bool AudioClockReadFailureIsDeviceFailure(
        bool clockValid,
        bool closing,
        int audioAnchorSerial,
        int currentSerial,
        int requestedSerial)
    {
        return !clockValid &&
               !closing &&
               audioAnchorSerial == currentSerial &&
               currentSerial == requestedSerial;
    }

    private static double TimestampToSeconds(long timestamp, AVRational timeBase)
    {
        return timestamp == ffmpeg.AV_NOPTS_VALUE ? double.NaN : timestamp * ffmpeg.av_q2d(timeBase);
    }

    internal static double StreamEndPosition(double formatStartSeconds, double streamStartSeconds, double streamDurationSeconds)
    {
        if (!double.IsFinite(streamDurationSeconds) || streamDurationSeconds <= 0)
        {
            return double.NaN;
        }

        var origin = double.IsFinite(formatStartSeconds) ? formatStartSeconds : 0;
        var start = double.IsFinite(streamStartSeconds) ? streamStartSeconds : origin;
        return Math.Max(0, start - origin + streamDurationSeconds);
    }

    internal static double PlaybackDuration(double formatDuration, double videoEndPosition, double audioEndPosition)
    {
        if (double.IsFinite(formatDuration) && formatDuration > 0)
        {
            return formatDuration;
        }

        // Zero means that playback stream is absent. A non-finite value means a selected
        // stream has no trustworthy end; in that case a partial sibling duration must not be
        // promoted to an authoritative total because the unknown stream may continue longer.
        if (!double.IsFinite(videoEndPosition) || !double.IsFinite(audioEndPosition))
        {
            return 0;
        }

        var videoEnd = videoEndPosition > 0 ? videoEndPosition : 0;
        var audioEnd = audioEndPosition > 0 ? audioEndPosition : 0;
        return Math.Max(videoEnd, audioEnd);
    }

    private static string? DictionaryValue(AVDictionary* dictionary, string key)
    {
        var entry = ffmpeg.av_dict_get(dictionary, key, null, 0);
        return entry == null ? null : Marshal.PtrToStringUTF8((IntPtr)entry->value);
    }

    /// <summary>
    /// Everything that belongs to one opened file: native contexts, queues, threads and clock.
    /// A new file gets a new session, so no per-file state can leak from one load to the next.
    /// </summary>
    private sealed class Session : IDisposable
    {
        private readonly FfmpegPlayer _owner;
        private readonly string _fileName;
        private AVFormatContext* _format;
        private readonly int _videoStreamIndex = -1;
        private volatile int _audioStreamIndex = -1;
        private readonly List<int> _audioStreamIndexes = new();
        private readonly Dictionary<int, double> _playbackStreamEndPositions = new();
        private readonly double _startTimeSeconds;
        private readonly double _formatDuration;

        private readonly PacketQueue _videoPackets = new();
        private readonly PacketQueue _audioPackets = new();
        private readonly VideoFrameQueue _videoFrames = new(VideoQueueCapacity);
        private readonly IAudioSink _audioSink;
        private readonly bool _hasAudio;
        private readonly bool _hasVideo;

        private Thread? _demuxThread;
        private Thread? _videoThread;
        private Thread? _audioThread;
        private Thread? _presentThread;
        private volatile bool _closing;

        private readonly AutoResetEvent _demuxWake = new(false);
        private readonly AutoResetEvent _presentWake = new(false);

        // Seek state. _requestedSerial is bumped for every seek request; the demux thread
        // performs the newest one and publishes it as the queue serial.
        private readonly Lock _seekLock = new();
        private int _requestedSerial;
        private double _requestedTarget = -1;
        private int _requestedAudioStreamIndex = -1;
        private bool _seekPending;
        private int _currentSerial; // serial the pipeline currently runs under

        // Clock.
        private readonly Stopwatch _wallClock = new();
        private double _wallClockBase; // media seconds at _wallClock zero
        private volatile bool _playing;
        private double _pausedPosition;
        private volatile bool _endReached;

        private double _audioAnchorPts = double.NaN; // media time of the first sample written since the last sink reset
        private int _audioAnchorSerial = -1;
        private double _audioSpeed = 1.0; // speed the audio currently queued was resampled for
        private int _audioEofSerial = -1;
        private double _audioEndPosition = double.NaN; // media position represented by all PCM queued for the current serial
        private bool _audioSinkFailed;

        // Restart tracking (see IVideoPlayer.HasPlaybackRestartedSince).
        private long _lastRestartTimestamp;
        private int _restartSerial = -1;

        private volatile float _gain = 1f;
        private double _speed = 1.0;

        public double Duration => PlaybackDuration(
            _formatDuration,
            SelectedStreamEndPosition(_videoStreamIndex),
            SelectedStreamEndPosition(_audioStreamIndex));

        public int VideoWidth { get; }
        public int VideoHeight { get; }
        public double DisplayAspectRatio { get; }

        // Kept in a static so the native function pointer handed to libavformat stays valid.
        private static readonly AVIOInterruptCB_callback InterruptCallbackDelegate = InterruptCallback;

        // Handed to libavformat as the interrupt callback's opaque; freed with the format context.
        private GCHandle _selfHandle;

        /// <summary>
        /// libavformat polls this during blocking reads and seeks: answering 1 makes the call
        /// return AVERROR_EXIT, so a demux thread stuck in av_read_frame on a slow or network
        /// file lets go when the session closes instead of outliving the format context.
        /// </summary>
        private static int InterruptCallback(void* opaque)
        {
            if (opaque == null)
            {
                return 0;
            }

            return GCHandle.FromIntPtr((IntPtr)opaque).Target is Session { _closing: true } ? 1 : 0;
        }

        public Session(FfmpegPlayer owner, string fileName)
        {
            _owner = owner;
            _fileName = fileName;

            var format = ffmpeg.avformat_alloc_context();
            if (format == null)
            {
                throw new InvalidOperationException("avformat_alloc_context failed");
            }

            _selfHandle = GCHandle.Alloc(this);
            format->interrupt_callback.callback = InterruptCallbackDelegate;
            format->interrupt_callback.opaque = (void*)GCHandle.ToIntPtr(_selfHandle);

            // On failure avformat_open_input frees the context and nulls the pointer itself.
            var result = ffmpeg.avformat_open_input(&format, NativeMediaPath.ForMpv(fileName), null, null);
            if (result < 0)
            {
                Dispose();
                throw new InvalidOperationException($"avformat_open_input: {FfmpegLibraries.ErrorText(result)}");
            }

            _format = format;
            result = ffmpeg.avformat_find_stream_info(format, null);
            if (result < 0)
            {
                Dispose();
                throw new InvalidOperationException($"avformat_find_stream_info: {FfmpegLibraries.ErrorText(result)}");
            }

            _startTimeSeconds = format->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : format->start_time / (double)ffmpeg.AV_TIME_BASE;

            _videoStreamIndex = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (_videoStreamIndex >= 0 && (format->streams[_videoStreamIndex]->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0)
            {
                _videoStreamIndex = -1; // cover art in an audio file is not a video track
            }

            _audioStreamIndex = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            for (var i = 0; i < (int)format->nb_streams; i++)
            {
                if (format->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    _audioStreamIndexes.Add(i);
                }
            }

            _hasVideo = _videoStreamIndex >= 0;
            _hasAudio = _audioStreamIndex >= 0;
            if (!_hasVideo && !_hasAudio)
            {
                Dispose();
                throw new InvalidOperationException("No audio or video stream found");
            }

            // Cache only streams this player can actually reproduce. Duration is read from UI
            // threads while teardown may close _format, so the public getter must never walk
            // native AVStream pointers after construction.
            if (_hasVideo)
            {
                var stream = format->streams[_videoStreamIndex];
                _playbackStreamEndPositions[_videoStreamIndex] = StreamEndPosition(
                    _startTimeSeconds,
                    TimestampToSeconds(stream->start_time, stream->time_base),
                    TimestampToSeconds(stream->duration, stream->time_base));
            }

            foreach (var audioIndex in _audioStreamIndexes)
            {
                var stream = format->streams[audioIndex];
                _playbackStreamEndPositions[audioIndex] = StreamEndPosition(
                    _startTimeSeconds,
                    TimestampToSeconds(stream->start_time, stream->time_base),
                    TimestampToSeconds(stream->duration, stream->time_base));
            }

            if (_hasVideo)
            {
                var parameters = format->streams[_videoStreamIndex]->codecpar;
                VideoWidth = parameters->width;
                VideoHeight = parameters->height;
                var sar = parameters->sample_aspect_ratio;
                var sarValue = sar.num > 0 && sar.den > 0 ? ffmpeg.av_q2d(sar) : 1.0;
                DisplayAspectRatio = VideoHeight > 0 ? VideoWidth * sarValue / VideoHeight : 0;
            }

            var duration = format->duration == ffmpeg.AV_NOPTS_VALUE ? double.NaN : format->duration / (double)ffmpeg.AV_TIME_BASE;
            _formatDuration = double.IsFinite(duration) && duration > 0 ? duration : 0;

            _audioSink = CreateAudioSink();
            if (_hasAudio)
            {
                try
                {
                    _audioSink.Open(OutputSampleRate, OutputChannels);
                }
                catch (Exception exception)
                {
                    Se.LogError(exception, "ffmpeg player: audio device failed to open, playing without sound");
                    _audioSink.Dispose();
                    _audioSink = new SilentAudioSink();
                    _audioSink.Open(OutputSampleRate, OutputChannels);
                }

                _audioSink.Pause();
            }
        }

        private double SelectedStreamEndPosition(int streamIndex)
        {
            return streamIndex >= 0 && _playbackStreamEndPositions.TryGetValue(streamIndex, out var endPosition)
                ? endPosition
                : 0;
        }

        public bool IsPlaying => _playing;

        public double Volume
        {
            set => _gain = (float)(value / 100.0);
        }

        public double Speed
        {
            get => _speed;
            set
            {
                if (Math.Abs(_speed - value) < 0.0001)
                {
                    return;
                }

                // Capture the playhead while Clock() still uses the old rate. For video-only
                // playback (or before audio has anchored), changing _speed first would reinterpret
                // the entire elapsed wall-clock interval at the new rate and jump the seek target.
                var position = Position;
                _speed = value;

                // The queued audio was resampled for the old speed and the clocks were anchored
                // under it; a seek to where we are re-anchors everything at the new speed.
                Seek(position);
            }
        }

        public void Start()
        {
            _demuxThread = new Thread(DemuxLoop) { IsBackground = true, Name = "ffmpeg demux" };
            _presentThread = new Thread(PresentLoop) { IsBackground = true, Name = "ffmpeg present" };
            _demuxThread.Start();
            _presentThread.Start();

            if (_hasVideo)
            {
                _videoThread = new Thread(VideoLoop) { IsBackground = true, Name = "ffmpeg video" };
                _videoThread.Start();
            }

            if (_hasAudio)
            {
                _audioThread = new Thread(AudioLoop) { IsBackground = true, Name = "ffmpeg audio" };
                _audioThread.Start();
            }
        }

        public void Play()
        {
            if (_playing)
            {
                return;
            }

            var hasOutstandingSeek = HasOutstandingSeek();
            if (ShouldAutoRewindOnPlay(hasOutstandingSeek, _endReached, Duration, Position))
            {
                Seek(0);
            }

            _playing = true;
            _wallClockBase = _pausedPosition;
            _wallClock.Restart();
            _audioSink.Resume();
            _presentWake.Set();
        }

        public void Pause()
        {
            if (!_playing)
            {
                return;
            }

            _pausedPosition = Clock();
            _playing = false;
            _wallClock.Stop();
            _audioSink.Pause();
            _presentWake.Set();
        }

        public double Position
        {
            get
            {
                lock (_seekLock)
                {
                    // Until the seek has landed the target is the truth - the pipeline still
                    // holds pictures from before it.
                    if (_seekPending || _restartSerial < _requestedSerial)
                    {
                        return _requestedTarget >= 0 ? _requestedTarget : _pausedPosition;
                    }
                }

                if (_endReached)
                {
                    return _pausedPosition;
                }

                return _playing ? Clock() : _pausedPosition;
            }
        }

        public void Seek(double seconds)
        {
            lock (_seekLock)
            {
                _requestedSerial++;
                _requestedTarget = seconds;
                _seekPending = true;
            }

            // Keep the committed playhead/end state unchanged until libavformat accepts the seek.
            // Position reports _requestedTarget while the request is outstanding, so optimistic
            // mutation here is unnecessary and would corrupt state if av_seek_frame fails.
            _demuxWake.Set();
        }

        public bool HasPlaybackRestartedSince(long stopwatchTimestamp)
        {
            if (Interlocked.Read(ref _lastRestartTimestamp) <= stopwatchTimestamp)
            {
                return false;
            }

            lock (_seekLock)
            {
                return _restartSerial >= _requestedSerial;
            }
        }

        public AudioTrackInfo? ToggleAudioTrack()
        {
            if (_audioStreamIndexes.Count < 2)
            {
                return null;
            }

            var position = Position;
            int next;
            lock (_seekLock)
            {
                next = NextAudioStreamIndex(_audioStreamIndexes, _audioStreamIndex, _requestedAudioStreamIndex);
                _requestedSerial++;
                _requestedTarget = position;
                _requestedAudioStreamIndex = next;
                _seekPending = true;
            }

            // Keep routing the committed track until av_seek_frame succeeds. Otherwise packets
            // from the requested track can enter the old serial, and a failed seek leaves the
            // decoder switched even though the playback pipeline never moved.
            _demuxWake.Set();

            var stream = _format->streams[next];
            return new AudioTrackInfo
            {
                Id = _audioStreamIndexes.IndexOf(next) + 1,
                FfIndex = next,
                Language = DictionaryValue(stream->metadata, "language"),
                Title = DictionaryValue(stream->metadata, "title"),
                IsSelected = true,
                IsDefault = (stream->disposition & ffmpeg.AV_DISPOSITION_DEFAULT) != 0,
                Codec = ffmpeg.avcodec_get_name(stream->codecpar->codec_id),
                Channels = stream->codecpar->ch_layout.nb_channels,
            };
        }

        /// <summary>Current media time while playing.</summary>
        private double Clock()
        {
            var clockFailed = false;
            var failedPosition = 0.0;

            if (_hasAudio)
            {
                lock (_seekLock)
                {
                    if (!_audioSinkFailed &&
                        !double.IsNaN(_audioAnchorPts) &&
                        _audioAnchorSerial == _currentSerial)
                    {
                        var clockValid = _audioSink.TryGetPlayedSeconds(out var playedSeconds);
                        var audioPosition = _audioAnchorPts + playedSeconds * _audioSpeed;
                        if (clockValid)
                        {
                            return audioPosition;
                        }

                        if (AudioClockReadFailureIsDeviceFailure(
                                clockValid,
                                _closing,
                                _audioAnchorSerial,
                                _currentSerial,
                                _requestedSerial))
                        {
                            FailOverAudioClockLocked(audioPosition);
                            clockFailed = true;
                            failedPosition = audioPosition;
                        }
                        else
                        {
                            // A newer seek already owns the future state. Keep reporting the last
                            // known old-serial position until that transaction commits or rolls back.
                            return audioPosition;
                        }
                    }
                }
            }

            if (clockFailed)
            {
                Se.LogError("ffmpeg player: audio clock query failed, continuing with wall-clock timing");
                _presentWake.Set();
                return failedPosition;
            }

            return WallClockPosition(_wallClockBase, _wallClock.Elapsed.TotalSeconds, _speed);
        }

        private bool HasOutstandingSeek()
        {
            lock (_seekLock)
            {
                return _requestedSerial != _currentSerial;
            }
        }

        /// <summary>True when a seek newer than the given serial has been requested (performed or not).</summary>
        private bool SeekRequestedSince(int serial)
        {
            lock (_seekLock)
            {
                return _requestedSerial != serial;
            }
        }

        // ---------------------------------------------------------------- demux

        private void DemuxLoop()
        {
            var eof = false;
            try
            {
                while (!_closing)
                {
                    if (TryTakeSeek(out var target, out var serial, out var audioStreamIndex))
                    {
                        if (PerformSeek(target, serial, audioStreamIndex))
                        {
                            eof = false;
                        }

                        continue;
                    }

                    if (eof)
                    {
                        _demuxWake.WaitOne(100);
                        continue;
                    }

                    if (_videoPackets.Bytes + _audioPackets.Bytes > MaxDemuxQueueBytes ||
                        ((!_hasVideo || _videoPackets.Count > MaxDemuxQueuePackets) &&
                         (!_hasAudio || _audioPackets.Count > MaxDemuxQueuePackets)))
                    {
                        _demuxWake.WaitOne(10);
                        continue;
                    }

                    var packet = ffmpeg.av_packet_alloc();
                    var result = ffmpeg.av_read_frame(_format, packet);
                    if (result < 0)
                    {
                        ffmpeg.av_packet_free(&packet);
                        if (result == -ffmpeg.EAGAIN)
                        {
                            _demuxWake.WaitOne(10);
                            continue;
                        }

                        // End of file - or a read error, which ffplay treats the same way.
                        eof = true;
                        _videoPackets.Push(null);
                        _audioPackets.Push(null);
                        continue;
                    }

                    if (packet->stream_index == _videoStreamIndex)
                    {
                        _videoPackets.Push(packet);
                    }
                    else if (packet->stream_index == _audioStreamIndex)
                    {
                        _audioPackets.Push(packet);
                    }
                    else
                    {
                        ffmpeg.av_packet_free(&packet);
                    }
                }
            }
            catch (Exception exception)
            {
                Se.LogError(exception, "ffmpeg player demux thread");
            }
        }

        private bool TryTakeSeek(out double target, out int serial, out int audioStreamIndex)
        {
            lock (_seekLock)
            {
                if (!_seekPending)
                {
                    target = 0;
                    serial = 0;
                    audioStreamIndex = -1;
                    return false;
                }

                _seekPending = false;
                target = _requestedTarget;
                serial = _requestedSerial;
                audioStreamIndex = _requestedAudioStreamIndex;
                return true;
            }
        }

        private bool PerformSeek(double target, int serial, int audioStreamIndex)
        {
            var timestamp = (long)((target + _startTimeSeconds) * ffmpeg.AV_TIME_BASE);
            var result = ffmpeg.av_seek_frame(_format, -1, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (result < 0)
            {
                Se.LogError($"ffmpeg player: seek to {target:0.###} s failed ({FfmpegLibraries.ErrorText(result)})");
                RollBackFailedSeek(serial);
                return false;
            }

            // Fence the sink first. Any old-serial writer that was already in flight is aborted
            // by the sink generation; any one that starts after this point is rejected by serial.
            _audioSink.Reset(serial);

            lock (_seekLock)
            {
                _currentSerial = serial;
                if (audioStreamIndex >= 0)
                {
                    _audioStreamIndex = audioStreamIndex;
                }

                if (_requestedSerial == serial)
                {
                    _requestedAudioStreamIndex = -1;
                }

                _audioAnchorPts = double.NaN;
                _audioAnchorSerial = -1;
                _audioSpeed = _speed;
                _audioEofSerial = -1;
                _audioEndPosition = double.NaN;
                _audioSinkFailed = false;
                _wallClockBase = target;
                if (_playing)
                {
                    _wallClock.Restart();
                }
                else
                {
                    _wallClock.Reset();
                }
            }

            _endReached = false;
            _videoPackets.Flush(serial, target);
            _audioPackets.Flush(serial, target);
            _videoFrames.Flush();
            _presentWake.Set();
            return true;
        }

        private void RollBackFailedSeek(int serial)
        {
            // Seek no longer mutates the committed paused position, so this remains the actual
            // position when paused. While playing, Clock() follows the still-current pipeline.
            var currentPosition = _playing ? Clock() : _pausedPosition;

            lock (_seekLock)
            {
                var requestedSerial = _requestedSerial;
                var state = FailedSeekState(
                    serial,
                    _currentSerial,
                    requestedSerial,
                    _requestedTarget,
                    currentPosition);

                _requestedAudioStreamIndex = FailedSeekAudioStreamState(
                    serial,
                    requestedSerial,
                    _requestedAudioStreamIndex);
                _requestedSerial = state.RequestedSerial;
                _requestedTarget = state.RequestedTarget;
            }
            _presentWake.Set();
        }

        // ---------------------------------------------------------------- video decode

        private void VideoLoop()
        {
            AVCodecContext* codec = null;
            AVFrame* frame = null;
            AVFrame* transferFrame = null; // hardware pictures are copied into this one
            SwsContext* sws = null;
            var swsSourceWidth = 0;
            var swsSourceHeight = 0;
            var swsSourceFormat = AVPixelFormat.AV_PIX_FMT_NONE;
            var outputWidth = 0;
            var outputHeight = 0;

            try
            {
                var stream = _format->streams[_videoStreamIndex];
                var hardware = HardwareDeviceTypes.Length > 0;
                codec = OpenDecoder(stream, hardware);
                hardware = codec->hw_device_ctx != null;
                _owner._decoderName = hardware ? HardwareDeviceName(codec) : string.Empty;
                frame = ffmpeg.av_frame_alloc();
                transferFrame = ffmpeg.av_frame_alloc();
                var timeBase = stream->time_base;
                var frameDuration = stream->avg_frame_rate.num > 0 && stream->avg_frame_rate.den > 0
                    ? 1.0 / ffmpeg.av_q2d(stream->avg_frame_rate)
                    : 1.0 / 25.0;

                var serial = -1;
                var dropUntil = -1.0;
                var presentedForSerial = false;
                var videoEndPosition = double.NaN;
                VideoFrame? lastDropped = null; // kept so a target past the last picture still shows something

                while (!_closing)
                {
                    if (!_videoPackets.TryPop(out var entry, 50))
                    {
                        continue;
                    }

                    if (entry.Serial != serial)
                    {
                        ffmpeg.avcodec_flush_buffers(codec);
                        serial = entry.Serial;
                        dropUntil = entry.SeekTarget;
                        presentedForSerial = false;
                        videoEndPosition = double.NaN;
                        _videoFrames.Return(lastDropped);
                        lastDropped = null;
                    }

                    var packet = entry.Packet;
                    var sendResult = ffmpeg.avcodec_send_packet(codec, packet); // null = drain at end of stream
                    if (packet != null)
                    {
                        ffmpeg.av_packet_free(&packet);
                    }

                    if (sendResult < 0 && sendResult != -ffmpeg.EAGAIN && sendResult != ffmpeg.AVERROR_EOF)
                    {
                        if (hardware)
                        {
                            // The hardware decoder rejected the stream - retry it in software.
                            FallBackToSoftware(ref codec, stream, sendResult, ref hardware);
                            serial = -1;
                        }

                        continue;
                    }

                    var hardwareFailed = false;
                    while (!_closing)
                    {
                        var receiveResult = ffmpeg.avcodec_receive_frame(codec, frame);
                        if (receiveResult < 0)
                        {
                            hardwareFailed = hardware && receiveResult != -ffmpeg.EAGAIN && receiveResult != ffmpeg.AVERROR_EOF;
                            break;
                        }

                        var picture = frame;
                        if (frame->hw_frames_ctx != null)
                        {
                            // The picture lives in GPU memory (IOSurface, D3D11 texture, DXVA2
                            // surface); pull it into system memory (NV12 typically) so swscale can
                            // convert it like a software picture.
                            receiveResult = ffmpeg.av_hwframe_transfer_data(transferFrame, frame, 0);
                            if (receiveResult < 0)
                            {
                                ffmpeg.av_frame_unref(frame);
                                hardwareFailed = true;
                                break;
                            }

                            transferFrame->pts = frame->pts;
                            transferFrame->best_effort_timestamp = frame->best_effort_timestamp;
                            picture = transferFrame;
                        }

                        var pts = TimestampToSeconds(picture->best_effort_timestamp, timeBase);
                        if (double.IsNaN(pts))
                        {
                            pts = TimestampToSeconds(picture->pts, timeBase);
                        }

                        if (double.IsNaN(pts))
                        {
                            pts = double.IsFinite(videoEndPosition)
                                ? videoEndPosition
                                : Math.Max(0, dropUntil);
                        }
                        else
                        {
                            pts -= _startTimeSeconds;
                        }

                        var decodedEnd = pts + frameDuration;
                        videoEndPosition = double.IsFinite(videoEndPosition)
                            ? Math.Max(videoEndPosition, decodedEnd)
                            : decodedEnd;

                        var (targetWidth, targetHeight) = OutputSize(picture->width, picture->height);
                        var format = (AVPixelFormat)picture->format;
                        if (sws == null || swsSourceWidth != picture->width || swsSourceHeight != picture->height || swsSourceFormat != format ||
                            outputWidth != targetWidth || outputHeight != targetHeight)
                        {
                            if (sws != null)
                            {
                                ffmpeg.sws_freeContext(sws);
                            }

                            const int swsBilinear = 2;
                            sws = ffmpeg.sws_getContext(picture->width, picture->height, format, targetWidth, targetHeight,
                                AVPixelFormat.AV_PIX_FMT_BGRA, swsBilinear, null, null, null);
                            swsSourceWidth = picture->width;
                            swsSourceHeight = picture->height;
                            swsSourceFormat = format;
                            outputWidth = targetWidth;
                            outputHeight = targetHeight;
                        }

                        if (sws == null)
                        {
                            ffmpeg.av_frame_unref(frame);
                            ffmpeg.av_frame_unref(transferFrame);
                            continue;
                        }

                        var beforeTarget = dropUntil >= 0 && pts < dropUntil - frameDuration * 0.5;
                        if (beforeTarget && !presentedForSerial)
                        {
                            // Skip pictures between the key frame the seek landed on and the
                            // target, but remember the last one in case the stream ends first.
                            var dropped = ConvertFrame(picture, sws, targetWidth, targetHeight, serial, pts, reuse: lastDropped);
                            if (dropped != null)
                            {
                                lastDropped = dropped;
                            }

                            ffmpeg.av_frame_unref(frame);
                            ffmpeg.av_frame_unref(transferFrame);
                            continue;
                        }

                        var converted = ConvertFrame(picture, sws, targetWidth, targetHeight, serial, pts, reuse: null);
                        ffmpeg.av_frame_unref(frame);
                        ffmpeg.av_frame_unref(transferFrame);
                        if (converted == null)
                        {
                            break; // queue closed or serial changed while waiting for a buffer
                        }

                        _videoFrames.Return(lastDropped);
                        lastDropped = null;
                        presentedForSerial = true;
                        _videoFrames.Push(converted);
                        _presentWake.Set();
                    }

                    if (hardwareFailed)
                    {
                        // The hardware decoder could not decode or hand back this picture
                        // (unsupported profile, for example): reopen in software and replay from
                        // the key frame.
                        FallBackToSoftware(ref codec, stream, 0, ref hardware);
                        serial = -1;
                        Seek(Position);
                        continue;
                    }

                    if (entry.IsEndOfStream)
                    {
                        if (!presentedForSerial && lastDropped != null)
                        {
                            _videoFrames.Push(lastDropped);
                            lastDropped = null;
                            presentedForSerial = true;
                        }

                        var marker = _videoFrames.Rent(outputWidth, outputHeight, serial, ref _currentSerial);
                        if (marker != null)
                        {
                            marker.Serial = serial;
                            marker.IsEndOfStream = true;
                            marker.Pts = double.IsFinite(videoEndPosition)
                                ? Math.Max(0, videoEndPosition)
                                : Math.Max(0, dropUntil);
                            _videoFrames.Push(marker);
                        }

                        _presentWake.Set();
                    }
                }
            }
            catch (Exception exception)
            {
                Se.LogError(exception, "ffmpeg player video thread");
            }
            finally
            {
                if (sws != null)
                {
                    ffmpeg.sws_freeContext(sws);
                }

                if (frame != null)
                {
                    ffmpeg.av_frame_free(&frame);
                }

                if (transferFrame != null)
                {
                    ffmpeg.av_frame_free(&transferFrame);
                }

                if (codec != null)
                {
                    ffmpeg.avcodec_free_context(&codec);
                }
            }
        }

        /// <summary>
        /// Replaces the hardware decoder context with a software one. The caller's pointer is
        /// nulled before the new decoder is opened, so when OpenDecoder throws the caller's
        /// cleanup does not free the context a second time.
        /// </summary>
        private void FallBackToSoftware(ref AVCodecContext* codec, AVStream* stream, int error, ref bool hardware)
        {
            var reason = error < 0 ? FfmpegLibraries.ErrorText(error) : "picture transfer failed";
            Se.LogError($"ffmpeg player: {HardwareDeviceName(codec)} decoding failed for {ffmpeg.avcodec_get_name(stream->codecpar->codec_id)} ({reason}), falling back to software decoding");
            var old = codec;
            codec = null;
            ffmpeg.avcodec_free_context(&old);
            hardware = false;
            _owner._decoderName = string.Empty;
            codec = OpenDecoder(stream, hardware: false);
        }

        private static (int Width, int Height) OutputSize(int width, int height)
        {
            if (width <= MaxOutputWidth || width <= 0 || height <= 0)
            {
                return (width, height);
            }

            var scaledHeight = (int)Math.Round(height * (MaxOutputWidth / (double)width));
            return (MaxOutputWidth, Math.Max(2, scaledHeight & ~1));
        }

        private VideoFrame? ConvertFrame(AVFrame* frame, SwsContext* sws, int width, int height, int serial, double pts, VideoFrame? reuse)
        {
            var target = reuse != null && reuse.Width == width && reuse.Height == height
                ? reuse
                : _videoFrames.Rent(width, height, serial, ref _currentSerial);
            if (target == null)
            {
                return null;
            }

            if (reuse != null && !ReferenceEquals(reuse, target))
            {
                _videoFrames.Return(reuse);
            }

            var destination = new byte*[] { (byte*)target.Data, null, null, null };
            var destinationStride = new[] { target.Stride, 0, 0, 0 };
            ffmpeg.sws_scale(sws, frame->data, frame->linesize, 0, frame->height, destination, destinationStride);

            target.Pts = pts;
            target.Serial = serial;
            target.IsEndOfStream = false;
            return target;
        }

        /// <summary>
        /// Hardware decoder device types to try on this platform, in order of preference:
        /// VideoToolbox on macOS; D3D11VA on Windows with DXVA2 as the older fallback. Every one
        /// of them hands decoded pictures back as NV12 through av_hwframe_transfer_data, so the
        /// swscale stage does not care which was picked.
        /// </summary>
        private static readonly AVHWDeviceType[] HardwareDeviceTypes = OperatingSystem.IsMacOS()
            ? new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX }
            : OperatingSystem.IsWindows()
                ? new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2 }
                : Array.Empty<AVHWDeviceType>();

        // Kept in a static so the native function pointer handed to libavcodec stays valid.
        private static readonly AVCodecContext_get_format GetHardwareFormatDelegate = GetHardwareFormat;

        /// <summary>The surface pixel format a hardware device type decodes into.</summary>
        private static AVPixelFormat SurfaceFormat(AVHWDeviceType deviceType)
        {
            return deviceType switch
            {
                AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX => AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX,
                AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA => AVPixelFormat.AV_PIX_FMT_D3D11,
                AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2 => AVPixelFormat.AV_PIX_FMT_DXVA2_VLD,
                _ => AVPixelFormat.AV_PIX_FMT_NONE,
            };
        }

        private static AVHWDeviceType AttachedDeviceType(AVCodecContext* context)
        {
            return context->hw_device_ctx == null
                ? AVHWDeviceType.AV_HWDEVICE_TYPE_NONE
                : ((AVHWDeviceContext*)context->hw_device_ctx->data)->type;
        }

        private static string HardwareDeviceName(AVCodecContext* context)
        {
            var deviceType = AttachedDeviceType(context);
            return deviceType == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE
                ? "hardware"
                : ffmpeg.av_hwdevice_get_type_name(deviceType);
        }

        /// <summary>
        /// libavcodec's pixel-format negotiation: pick the surface format of the attached hardware
        /// device when it is offered, else let the default choose a software format.
        /// </summary>
        private static AVPixelFormat GetHardwareFormat(AVCodecContext* context, AVPixelFormat* formats)
        {
            var wanted = SurfaceFormat(AttachedDeviceType(context));
            if (wanted != AVPixelFormat.AV_PIX_FMT_NONE)
            {
                for (var p = formats; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
                {
                    if (*p == wanted)
                    {
                        return *p;
                    }
                }
            }

            return ffmpeg.avcodec_default_get_format(context, formats);
        }

        /// <summary>
        /// True when the device can transfer decoded pictures into at least one system-memory
        /// format; without one av_hwframe_transfer_data would fail on every frame.
        /// </summary>
        private static bool HasTransferFormats(AVBufferRef* device)
        {
            var constraints = ffmpeg.av_hwdevice_get_hwframe_constraints(device, null);
            if (constraints == null)
            {
                return false;
            }

            var hasFormats = constraints->valid_sw_formats != null && *constraints->valid_sw_formats != AVPixelFormat.AV_PIX_FMT_NONE;
            ffmpeg.av_hwframe_constraints_free(&constraints);
            return hasFormats;
        }

        /// <summary>True when the decoder can use a device context of the given type.</summary>
        private static bool SupportsHardwareDevice(AVCodec* decoder, AVHWDeviceType deviceType)
        {
            for (var i = 0; ; i++)
            {
                var config = ffmpeg.avcodec_get_hw_config(decoder, i);
                if (config == null)
                {
                    return false;
                }

                if (config->device_type == deviceType &&
                    (config->methods & (int)AvCodecHwConfigMethod.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0)
                {
                    return true;
                }
            }
        }

        /// <summary>
        /// Opens a decoder for the stream. With <paramref name="hardware"/> the first device type
        /// from <see cref="HardwareDeviceTypes"/> that the decoder supports and that can be created
        /// is attached; the caller can tell by <c>hw_device_ctx</c> being set. Any hardware setup
        /// failure silently means software.
        /// </summary>
        private AVCodecContext* OpenDecoder(AVStream* stream, bool hardware = false)
        {
            var decoder = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            if (decoder == null)
            {
                throw new InvalidOperationException($"No decoder for {ffmpeg.avcodec_get_name(stream->codecpar->codec_id)}");
            }

            var codec = ffmpeg.avcodec_alloc_context3(decoder);
            if (codec == null)
            {
                throw new InvalidOperationException("avcodec_alloc_context3 failed");
            }

            var result = ffmpeg.avcodec_parameters_to_context(codec, stream->codecpar);
            if (result < 0)
            {
                ffmpeg.avcodec_free_context(&codec);
                throw new InvalidOperationException($"avcodec_parameters_to_context: {FfmpegLibraries.ErrorText(result)}");
            }

            codec->pkt_timebase = stream->time_base;
            codec->thread_count = 0; // auto

            if (hardware && stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                foreach (var deviceType in HardwareDeviceTypes)
                {
                    if (!SupportsHardwareDevice(decoder, deviceType))
                    {
                        continue;
                    }

                    AVBufferRef* device = null;
                    if (ffmpeg.av_hwdevice_ctx_create(&device, deviceType, null, null, 0) < 0)
                    {
                        continue;
                    }

                    if (!HasTransferFormats(device))
                    {
                        // The device came up but cannot hand pictures back to system memory
                        // (seen with D3D11VA on some drivers): try the next type, else software.
                        Se.LogError($"ffmpeg player: {ffmpeg.av_hwdevice_get_type_name(deviceType)} reports no transfer formats, skipping");
                        ffmpeg.av_buffer_unref(&device);
                        continue;
                    }

                    codec->hw_device_ctx = device; // freed with the codec context
                    codec->get_format = GetHardwareFormatDelegate;
                    break;
                }
            }

            result = ffmpeg.avcodec_open2(codec, decoder, null);
            if (result < 0)
            {
                ffmpeg.avcodec_free_context(&codec);
                throw new InvalidOperationException($"avcodec_open2: {FfmpegLibraries.ErrorText(result)}");
            }

            return codec;
        }

        // ---------------------------------------------------------------- audio decode

        private void AudioLoop()
        {
            AVCodecContext* codec = null;
            AVFrame* frame = null;
            SwrContext* swr = null;
            var swrSampleRate = 0;
            var swrFormat = AVSampleFormat.AV_SAMPLE_FMT_NONE;
            var swrChannels = 0;
            var swrSpeed = 0.0;
            var codecStreamIndex = -1;
            byte[] pcm = [];

            try
            {
                frame = ffmpeg.av_frame_alloc();
                var serial = -1;
                var dropUntil = -1.0;
                var timeBase = default(AVRational);
                var anchored = false;
                var queuedAudioEnd = double.NaN;
                var inferredAudioPts = 0.0;

                while (!_closing)
                {
                    if (!_audioPackets.TryPop(out var entry, 50))
                    {
                        continue;
                    }

                    var packet = entry.Packet;
                    if (packet != null && packet->stream_index != codecStreamIndex)
                    {
                        if (codec != null)
                        {
                            ffmpeg.avcodec_free_context(&codec);
                        }

                        codecStreamIndex = packet->stream_index;
                        var stream = _format->streams[codecStreamIndex];
                        timeBase = stream->time_base;
                        codec = OpenDecoder(stream);
                    }

                    if (codec == null)
                    {
                        if (packet != null)
                        {
                            ffmpeg.av_packet_free(&packet);
                        }

                        continue;
                    }

                    if (entry.Serial != serial)
                    {
                        ffmpeg.avcodec_flush_buffers(codec);
                        serial = entry.Serial;
                        dropUntil = entry.SeekTarget;
                        anchored = false;
                        queuedAudioEnd = double.NaN;
                        inferredAudioPts = Math.Max(0, dropUntil);
                        if (swr != null)
                        {
                            ffmpeg.swr_free(&swr); // forget buffered samples from before the seek
                        }
                    }

                retryAudioPacket:
                    var sendResult = ffmpeg.avcodec_send_packet(codec, packet);
                    var packetRejected = sendResult == -ffmpeg.EAGAIN;
                    if (!packetRejected && packet != null)
                    {
                        ffmpeg.av_packet_free(&packet);
                    }

                    if (sendResult < 0 && !packetRejected && sendResult != ffmpeg.AVERROR_EOF)
                    {
                        continue;
                    }

                    var receiveResult = 0;
                    var receivedOutput = false;
                    while (!_closing)
                    {
                        receiveResult = ffmpeg.avcodec_receive_frame(codec, frame);
                        if (receiveResult < 0)
                        {
                            break;
                        }

                        receivedOutput = true;
                        var pts = TimestampToSeconds(frame->best_effort_timestamp, timeBase);
                        if (double.IsNaN(pts))
                        {
                            pts = TimestampToSeconds(frame->pts, timeBase);
                        }

                        if (double.IsNaN(pts))
                        {
                            pts = inferredAudioPts;
                        }
                        else
                        {
                            pts -= _startTimeSeconds;
                        }

                        var frameSeconds = frame->sample_rate > 0 ? frame->nb_samples / (double)frame->sample_rate : 0;
                        inferredAudioPts = Math.Max(inferredAudioPts, pts + frameSeconds);
                        if (dropUntil >= 0 && !anchored && pts + frameSeconds < dropUntil)
                        {
                            ffmpeg.av_frame_unref(frame);
                            continue;
                        }

                        var speed = _speed;
                        var format = (AVSampleFormat)frame->format;
                        if (swr == null || swrSampleRate != frame->sample_rate || swrFormat != format ||
                            swrChannels != frame->ch_layout.nb_channels || Math.Abs(swrSpeed - speed) > 0.0001)
                        {
                            if (swr != null)
                            {
                                ffmpeg.swr_free(&swr);
                            }

                            AVChannelLayout outLayout;
                            ffmpeg.av_channel_layout_default(&outLayout, OutputChannels);
                            var inLayout = frame->ch_layout;
                            SwrContext* newSwr = null;

                            // Speed is done by resampling: the device keeps playing at the output
                            // rate, so producing fewer/more samples per input second makes the
                            // audio faster/slower (with a pitch change, like a tape).
                            var outRate = Math.Max(8000, (int)Math.Round(OutputSampleRate / speed));
                            var setResult = ffmpeg.swr_alloc_set_opts2(&newSwr, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, outRate,
                                &inLayout, format, frame->sample_rate, 0, null);
                            ffmpeg.av_channel_layout_uninit(&outLayout);
                            if (setResult < 0 || newSwr == null || ffmpeg.swr_init(newSwr) < 0)
                            {
                                if (newSwr != null)
                                {
                                    ffmpeg.swr_free(&newSwr);
                                }

                                ffmpeg.av_frame_unref(frame);
                                continue;
                            }

                            swr = newSwr;
                            swrSampleRate = frame->sample_rate;
                            swrFormat = format;
                            swrChannels = frame->ch_layout.nb_channels;
                            swrSpeed = speed;
                        }

                        // Trim the part of the first frame that lies before the seek target.
                        var skipInputSamples = 0;
                        if (dropUntil >= 0 && !anchored && pts < dropUntil && frame->sample_rate > 0)
                        {
                            skipInputSamples = Math.Min(frame->nb_samples, (int)((dropUntil - pts) * frame->sample_rate));
                        }

                        var outSamplesMax = (int)(frame->nb_samples * (double)OutputSampleRate / speed / frame->sample_rate) + 256;
                        var needed = outSamplesMax * OutputChannels * 2;
                        if (pcm.Length < needed)
                        {
                            pcm = new byte[needed];
                        }

                        int written;
                        var input = frame->extended_data;
                        var inputSamples = frame->nb_samples;
                        // Only the first frame after a seek is trimmed, so this small array is
                        // allocated once per seek, not per frame (a stackalloc here would grow the
                        // stack on every trimmed frame of the decode loop).
                        byte*[]? shifted = null;
                        if (skipInputSamples > 0)
                        {
                            inputSamples -= skipInputSamples;
                            // Planar or packed, the offset in bytes per plane is samples * bytesPerSample * (channels for packed).
                            var planar = IsPlanarSampleFormat(format);
                            var bytesPerSample = ffmpeg.av_get_bytes_per_sample(format);
                            var planes = planar ? frame->ch_layout.nb_channels : 1;
                            var offset = skipInputSamples * bytesPerSample * (planar ? 1 : frame->ch_layout.nb_channels);
                            shifted = new byte*[planes];
                            for (var p = 0; p < planes; p++)
                            {
                                shifted[p] = input[p] + offset;
                            }
                        }

                        fixed (byte* pcmPtr = pcm)
                        fixed (byte** shiftedPtr = shifted)
                        {
                            var output = pcmPtr;
                            var inputPtr = shiftedPtr != null ? shiftedPtr : input;
                            written = inputSamples > 0
                                ? ffmpeg.swr_convert(swr, &output, outSamplesMax, inputPtr, inputSamples)
                                : 0;
                        }

                        var samplePts = pts + skipInputSamples / (double)Math.Max(1, frame->sample_rate);
                        ffmpeg.av_frame_unref(frame);
                        if (written <= 0)
                        {
                            continue;
                        }

                        var bytes = written * OutputChannels * 2;

                        if (!WriteAudioChunk(
                                pcm,
                                bytes,
                                serial,
                                samplePts,
                                speed,
                                ref queuedAudioEnd,
                                out var sinkAccepted))
                        {
                            break;
                        }

                        if (!anchored && sinkAccepted)
                        {
                            lock (_seekLock)
                            {
                                if (!AudioWriteCanAnchor(sinkAccepted, serial, _currentSerial))
                                {
                                    break; // a newer seek landed after this chunk was accepted
                                }

                                _audioAnchorPts = samplePts;
                                _audioAnchorSerial = serial;
                                _audioSpeed = speed;
                                if (!_hasVideo && serial > _restartSerial)
                                {
                                    // No picture will ever land this seek - the first accepted audio does.
                                    _restartSerial = serial;
                                    if (!_playing)
                                    {
                                        _pausedPosition = samplePts;
                                    }

                                    Interlocked.Exchange(ref _lastRestartTimestamp, Stopwatch.GetTimestamp());
                                }
                            }

                            anchored = true;
                        }
                    }

                    var interrupted = _closing ||
                                      SeekRequestedSince(serial) ||
                                      ShouldDrainAudioAfterSinkFailure();
                    if (ShouldResendPacket(sendResult, receivedOutput, interrupted))
                    {
                        goto retryAudioPacket;
                    }

                    if (packetRejected && !interrupted && !receivedOutput)
                    {
                        Se.LogError("ffmpeg player: decoder returned EAGAIN without output; dropping rejected audio packet");
                    }

                    if (packet != null)
                    {
                        ffmpeg.av_packet_free(&packet);
                    }

                    if (entry.IsEndOfStream && !_closing &&
                        (sendResult == ffmpeg.AVERROR_EOF || (sendResult >= 0 && receiveResult == ffmpeg.AVERROR_EOF)))
                    {
                        // libswresample can retain delayed output after the decoder itself is
                        // fully drained. Flush it before publishing audio EOF or the tail is cut.
                        if (swr != null && !FlushResampler(swr, ref pcm, swrSpeed, serial, ref queuedAudioEnd))
                        {
                            continue;
                        }

                        lock (_seekLock)
                        {
                            if (serial != _currentSerial || serial != _requestedSerial)
                            {
                                continue; // a newer seek superseded this EOF
                            }

                            _audioEofSerial = serial;
                            _audioEndPosition = queuedAudioEnd;
                        }

                        _presentWake.Set();
                    }
                }
            }
            catch (Exception exception)
            {
                Se.LogError(exception, "ffmpeg player audio thread");
            }
            finally
            {
                if (swr != null)
                {
                    ffmpeg.swr_free(&swr);
                }

                if (frame != null)
                {
                    ffmpeg.av_frame_free(&frame);
                }

                if (codec != null)
                {
                    ffmpeg.avcodec_free_context(&codec);
                }
            }
        }

        private bool ShouldDrainAudioAfterSinkFailure()
        {
            lock (_seekLock)
            {
                return _audioSinkFailed;
            }
        }

        private bool TryFailOverAudioClock(int serial)
        {
            lock (_seekLock)
            {
                if (!AudioWriteFailureIsDeviceFailure(
                        writeAccepted: false,
                        _closing,
                        serial,
                        _currentSerial,
                        _requestedSerial))
                {
                    return false;
                }

                _audioSink.TryGetPlayedSeconds(out var playedSeconds);
                var wallClockPosition = _wallClockBase + _wallClock.Elapsed.TotalSeconds * _speed;
                var position = AudioClockFailoverPosition(
                    _audioAnchorPts,
                    _audioAnchorSerial,
                    serial,
                    playedSeconds,
                    _audioSpeed,
                    wallClockPosition);

                FailOverAudioClockLocked(position);
            }

            Se.LogError("ffmpeg player: audio output failed, continuing with wall-clock timing");
            _presentWake.Set();
            return true;
        }

        /// <summary>Transitions from an audio-device clock to wall-clock timing. Called under _seekLock.</summary>
        private void FailOverAudioClockLocked(double position)
        {
            _audioSinkFailed = true;
            _audioAnchorPts = double.NaN;
            _audioAnchorSerial = -1;
            _wallClockBase = position;
            if (_playing)
            {
                _wallClock.Restart();
            }
            else
            {
                _pausedPosition = position;
                _wallClock.Reset();
            }
        }

        private bool WriteAudioChunk(
            byte[] pcm,
            int bytes,
            int serial,
            double samplePts,
            double speed,
            ref double queuedAudioEnd,
            out bool sinkAccepted)
        {
            sinkAccepted = false;
            if (bytes <= 0)
            {
                return true;
            }

            // After a real device failure the decoder must keep running so unknown-duration
            // streams still produce timestamps/EOF. Skip native output, but keep media-end
            // accounting so the wall clock can finish at the observed end.
            var decodeOnly = ShouldDrainAudioAfterSinkFailure();
            if (!decodeOnly)
            {
                ApplyGain(pcm, bytes, _gain);
                sinkAccepted = _audioSink.Write(new ReadOnlySpan<byte>(pcm, 0, bytes), serial);
                if (!sinkAccepted)
                {
                    // Seek/reset/close rejection is a normal interruption. A rejection that still
                    // belongs to the current/latest serial is a device failure. Once failover is
                    // established, this chunk still contributes to media-end accounting even
                    // though it never became audible.
                    if (!TryFailOverAudioClock(serial))
                    {
                        return false;
                    }
                }
            }

            var chunkMediaSeconds = bytes / OutputBytesPerSecond * speed;
            var start = double.IsFinite(queuedAudioEnd) ? queuedAudioEnd : 0;
            if (double.IsFinite(samplePts))
            {
                start = Math.Max(start, samplePts);
            }

            queuedAudioEnd = start + chunkMediaSeconds;

            lock (_seekLock)
            {
                if (serial != _currentSerial || serial != _requestedSerial)
                {
                    return false;
                }

                _audioEndPosition = queuedAudioEnd;
            }

            return true;
        }

        private bool FlushResampler(
            SwrContext* swr,
            ref byte[] pcm,
            double speed,
            int serial,
            ref double queuedAudioEnd)
        {
            while (!_closing)
            {
                var outSamplesMax = ffmpeg.swr_get_out_samples(swr, 0);
                if (outSamplesMax <= 0)
                {
                    return true;
                }

                var needed = outSamplesMax * OutputChannels * 2;
                if (pcm.Length < needed)
                {
                    pcm = new byte[needed];
                }

                int written;
                fixed (byte* pcmPtr = pcm)
                {
                    var output = pcmPtr;
                    written = ffmpeg.swr_convert(swr, &output, outSamplesMax, null, 0);
                }

                if (written < 0)
                {
                    Se.LogError($"ffmpeg player: failed to flush audio resampler ({FfmpegLibraries.ErrorText(written)})");
                    return false;
                }

                if (written == 0)
                {
                    return true;
                }

                var bytes = written * OutputChannels * 2;
                if (!WriteAudioChunk(pcm, bytes, serial, double.NaN, speed, ref queuedAudioEnd, out _))
                {
                    return false;
                }
            }

            return false;
        }

        private static void ApplyGain(byte[] pcm, int bytes, float gain)
        {
            if (Math.Abs(gain - 1f) < 0.001f)
            {
                return;
            }

            var samples = MemoryMarshal.Cast<byte, short>(new Span<byte>(pcm, 0, bytes));
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (short)Math.Clamp((int)(samples[i] * gain), short.MinValue, short.MaxValue);
            }
        }

        // ---------------------------------------------------------------- present

        private void PresentLoop()
        {
            try
            {
                while (!_closing)
                {
                    var frame = _videoFrames.Peek();
                    if (frame == null)
                    {
                        if (!_hasVideo)
                        {
                            PresentAudioOnlyTick();
                        }

                        _presentWake.WaitOne(20);
                        continue;
                    }

                    int currentSerial;
                    lock (_seekLock)
                    {
                        currentSerial = _currentSerial;
                    }

                    if (frame.Serial != currentSerial)
                    {
                        _videoFrames.Return(_videoFrames.Pop());
                        continue;
                    }

                    if (frame.IsEndOfStream)
                    {
                        if (!_playing)
                        {
                            // Keep the marker: it is what tells a later Play that the end was
                            // reached, so the clock does not run on past the observed end.
                            _presentWake.WaitOne(50);
                            continue;
                        }

                        var observedEnd = frame.Pts;
                        if (SeekRequestedSince(frame.Serial))
                        {
                            // Keep the old EOS marker until the seek commits or fails. Without
                            // this guard a video-only stream can ReachEnd while av_seek_frame is
                            // still blocked and lose a Play issued for the requested destination.
                            _presentWake.WaitOne(20);
                            continue;
                        }

                        if (_hasAudio)
                        {
                            // Video can finish before audio. For a known duration, retain the old
                            // duration/stall fallback. For an unknown duration, wait for the audio
                            // decoder/resampler EOF of this seek serial and for the sink clock to
                            // reach the last PCM that was actually queued.
                            var interrupted = false;
                            var lastClock = double.NegativeInfinity;
                            var stalledSince = Stopwatch.GetTimestamp();
                            while (_playing && !_closing)
                            {
                                var now = Clock();
                                int audioEofSerial;
                                double audioEnd;
                                lock (_seekLock)
                                {
                                    audioEofSerial = _audioEofSerial;
                                    audioEnd = _audioEndPosition;
                                }

                                if (Duration > 0 && now >= Duration - 0.05)
                                {
                                    observedEnd = Math.Max(observedEnd, now);
                                    break;
                                }

                                if (AudioDrainComplete(audioEofSerial, frame.Serial, audioEnd, now))
                                {
                                    observedEnd = Math.Max(observedEnd,
                                        double.IsFinite(audioEnd) ? audioEnd : now);
                                    break;
                                }

                                if (now > lastClock + 0.0005)
                                {
                                    lastClock = now;
                                    stalledSince = Stopwatch.GetTimestamp();
                                }
                                else if ((Duration > 0 || audioEofSerial == frame.Serial) &&
                                         Stopwatch.GetElapsedTime(stalledSince).TotalSeconds > 0.5)
                                {
                                    // Known-duration legacy fallback, or a broken/stalled sink
                                    // after the decoder has explicitly said no more audio exists.
                                    observedEnd = Math.Max(observedEnd, now);
                                    break;
                                }

                                if (SeekRequestedSince(frame.Serial))
                                {
                                    interrupted = true;
                                    break;
                                }

                                var waitMs = Duration > 0
                                    ? Math.Clamp((int)((Duration - now) * 1000), 1, 200)
                                    : 20;
                                _presentWake.WaitOne(waitMs);
                            }

                            if (interrupted || !_playing || _closing || SeekRequestedSince(frame.Serial))
                            {
                                continue;
                            }

                            observedEnd = Math.Max(observedEnd, Clock());
                        }

                        var popped = _videoFrames.Pop();
                        _videoFrames.Return(popped);
                        if (!ReferenceEquals(popped, frame))
                        {
                            continue; // flushed by a seek while waiting
                        }

                        ReachEnd(observedEnd);
                        continue;
                    }

                    bool firstOfSerial;
                    lock (_seekLock)
                    {
                        firstOfSerial = _restartSerial < frame.Serial;
                    }

                    if (firstOfSerial)
                    {
                        // The seek has landed: show it right away, playing or paused.
                        ShowFrame(frame);
                        continue;
                    }

                    if (!_playing)
                    {
                        _presentWake.WaitOne(50);
                        continue;
                    }

                    var clock = Clock();
                    var delay = frame.Pts - clock;
                    if (delay <= 0.002)
                    {
                        // Late: drop everything but the last picture that is already due.
                        while (true)
                        {
                            var next = PeekSecond();
                            if (next == null || next.IsEndOfStream || next.Serial != frame.Serial || next.Pts > clock)
                            {
                                break;
                            }

                            _videoFrames.Return(_videoFrames.Pop());
                            frame = next;
                        }

                        ShowFrame(frame);
                        continue;
                    }

                    _presentWake.WaitOne(Math.Clamp((int)(delay * 1000), 1, 50));
                }
            }
            catch (Exception exception)
            {
                Se.LogError(exception, "ffmpeg player present thread");
            }
        }

        private VideoFrame? PeekSecond()
        {
            return _videoFrames.PeekSecond();
        }

        private void PresentAudioOnlyTick()
        {
            if (!_playing)
            {
                return;
            }

            var now = Clock();
            if (Duration > 0 && now >= Duration)
            {
                ReachEnd(now);
                return;
            }

            int currentSerial;
            int requestedSerial;
            int audioEofSerial;
            double audioEnd;
            lock (_seekLock)
            {
                currentSerial = _currentSerial;
                requestedSerial = _requestedSerial;
                audioEofSerial = _audioEofSerial;
                audioEnd = _audioEndPosition;
            }

            if (currentSerial == requestedSerial &&
                AudioDrainComplete(audioEofSerial, currentSerial, audioEnd, now))
            {
                ReachEnd(double.IsFinite(audioEnd) ? audioEnd : now);
            }
        }

        private void ReachEnd(double observedPosition)
        {
            _pausedPosition = EndPosition(Duration, observedPosition);
            _playing = false;
            _endReached = true;
            _wallClock.Stop();
            _audioSink.Pause();
        }

        private void ShowFrame(VideoFrame frame)
        {
            var popped = _videoFrames.Pop();
            if (!ReferenceEquals(popped, frame))
            {
                _videoFrames.Return(popped);
                return;
            }

            lock (_seekLock)
            {
                if (frame.Serial > _restartSerial)
                {
                    _restartSerial = frame.Serial;
                    if (!_playing)
                    {
                        _pausedPosition = frame.Pts;
                    }
                }
                else if (!_playing)
                {
                    _pausedPosition = frame.Pts;
                }
            }

            // Timestamp after the serial so HasPlaybackRestartedSince never sees a new
            // timestamp with an old serial.
            Interlocked.Exchange(ref _lastRestartTimestamp, Stopwatch.GetTimestamp());
            _owner.Present(frame, _videoFrames);
        }

        // ---------------------------------------------------------------- teardown

        public void Dispose()
        {
            _closing = true;
            _playing = false;
            _videoPackets.Close();
            _audioPackets.Close();
            _videoFrames.Close();
            _demuxWake.Set();
            _presentWake.Set();

            // The constructor disposes a half-built session (stream info failed, no usable
            // stream) before the sink exists, so the sink is null on those paths.
            var audioSink = _audioSink;
            if (audioSink != null)
            {
                try
                {
                    audioSink.Reset(int.MinValue);
                }
                catch
                {
                    // sink may not have been opened
                }
            }

            var stopped = JoinThread(_demuxThread);
            stopped &= JoinThread(_videoThread);
            stopped &= JoinThread(_audioThread);
            stopped &= JoinThread(_presentThread);

            audioSink?.Dispose();
            _demuxWake.Dispose();
            _presentWake.Dispose();

            if (!stopped)
            {
                // A worker is still inside libavformat/libavcodec with this context; closing it
                // now would be a use-after-free. Leak it (and the handle its interrupt callback
                // dereferences) rather than crash.
                Se.LogError($"ffmpeg player: leaking the format context of '{_fileName}' because a thread did not stop");
                return;
            }

            if (_format != null)
            {
                var format = _format;
                ffmpeg.avformat_close_input(&format);
                _format = null;
            }

            if (_selfHandle.IsAllocated)
            {
                _selfHandle.Free();
            }
        }

        /// <summary>False when the thread is still running after the timeout.</summary>
        private static bool JoinThread(Thread? thread)
        {
            if (thread == null || thread == Thread.CurrentThread)
            {
                return true;
            }

            if (thread.Join(TimeSpan.FromSeconds(5)))
            {
                return true;
            }

            Se.LogError($"ffmpeg player: thread '{thread.Name}' did not stop in time");
            return false;
        }
    }
}
