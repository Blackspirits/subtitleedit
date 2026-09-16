using Avalonia;
using FFmpeg.AutoGen;
using Nikse.SubtitleEdit.Features.Shared;
using Nikse.SubtitleEdit.Logic.VideoPlayers.Ffmpeg;
using Nikse.SubtitleEdit.Logic.VideoPlayers.Ffmpeg.Audio;
using System.IO.Compression;

namespace UITests.Logic;

/// <summary>
/// The pure parts of the FFmpeg video player: frame queue/pool bookkeeping, letterboxing, the
/// silent (clock-only) audio sink, and the library extraction filter. The decode pipeline itself
/// needs the FFmpeg shared libraries and is exercised through the app.
/// </summary>
public class FfmpegPlayerTests
{
    [Fact]
    public void FitRect_WiderControl_LetterboxesHorizontally()
    {
        var rect = FfmpegSoftwareControl.FitRect(new Rect(0, 0, 1000, 500), 16 / 9.0);

        Assert.Equal(500, rect.Height, 3);
        Assert.Equal(500 * 16 / 9.0, rect.Width, 3);
        Assert.Equal((1000 - rect.Width) / 2, rect.X, 3);
        Assert.Equal(0, rect.Y, 3);
    }

    [Fact]
    public void FitRect_TallerControl_LetterboxesVertically()
    {
        var rect = FfmpegSoftwareControl.FitRect(new Rect(0, 0, 400, 1000), 4 / 3.0);

        Assert.Equal(400, rect.Width, 3);
        Assert.Equal(300, rect.Height, 3);
        Assert.Equal(350, rect.Y, 3);
    }

    [Fact]
    public void FitRect_UnknownAspect_FillsBounds()
    {
        var bounds = new Rect(0, 0, 640, 480);
        Assert.Equal(bounds, FfmpegSoftwareControl.FitRect(bounds, 0));
    }

    [Fact]
    public void VideoFrame_StrideIsAlignedWithSlackForSwscaleOverrun()
    {
        // swscale writes whole SIMD vectors: a 680 px BGRA row (2720 bytes) overran a tight
        // buffer by 32 bytes on the last row and corrupted the heap.
        using var frame = new VideoFrame(680, 312);

        Assert.Equal(0, frame.Stride % 64);
        Assert.True(frame.Stride >= 680 * 4 + 32);
        Assert.NotEqual(IntPtr.Zero, frame.Data);
    }

    [Fact]
    public void VideoFrameQueue_ReturnsBuffersToPoolAndReusesThem()
    {
        var queue = new VideoFrameQueue(2);
        var serial = 1;

        var a = queue.Rent(16, 8, 1, ref serial);
        Assert.NotNull(a);
        queue.Push(a!);
        Assert.Equal(1, queue.Count);

        var popped = queue.Pop();
        Assert.Same(a, popped);
        queue.Return(popped);

        var b = queue.Rent(16, 8, 1, ref serial);
        Assert.Same(a, b); // pooled buffer, no new allocation

        queue.Close();
    }

    [Fact]
    public void VideoFrameQueue_Rent_GivesUpWhenSerialMoves()
    {
        var queue = new VideoFrameQueue(1);
        var serial = 1;
        queue.Push(queue.Rent(4, 4, 1, ref serial)!); // full

        var stale = 1;
        serial = 2; // a seek happened
        var frame = queue.Rent(4, 4, stale, ref serial);

        Assert.Null(frame);
        queue.Close();
    }

    [Fact]
    public void VideoFrameQueue_PeekSecond_SeesTheFrameBehindTheHead()
    {
        var queue = new VideoFrameQueue(3);
        var serial = 1;
        var first = queue.Rent(4, 4, 1, ref serial)!;
        var second = queue.Rent(4, 4, 1, ref serial)!;
        queue.Push(first);
        Assert.Null(queue.PeekSecond());
        queue.Push(second);

        Assert.Same(first, queue.Peek());
        Assert.Same(second, queue.PeekSecond());
        queue.Close();
    }

    [Theory]
    [InlineData(7, 7, true)]
    [InlineData(7, 8, false)]
    public void CanPublishVideoFrame_RequiresCurrentSeekSerial(int frameSerial, int currentSerial, bool expected)
    {
        Assert.Equal(expected, FfmpegPlayer.CanPublishVideoFrame(frameSerial, currentSerial));
    }

    [Fact]
    public void VideoFrameQueue_SizeChange_DropsOldPool()
    {
        var queue = new VideoFrameQueue(2);
        var serial = 1;
        var small = queue.Rent(4, 4, 1, ref serial)!;
        queue.Return(small);

        var big = queue.Rent(8, 8, 1, ref serial)!;
        Assert.NotSame(small, big);
        Assert.Equal(8, big.Width);

        queue.Return(small); // wrong size now - must be disposed, not pooled
        Assert.Equal(System.IntPtr.Zero, small.Data);
        queue.Close();
    }

    [Fact]
    public void SilentAudioSink_PlayedSecondsNeverExceedsWrittenAudio()
    {
        using var sink = new SilentAudioSink();
        sink.Open(48000, 2);
        sink.Resume();

        Assert.True(sink.Write(new byte[48000 * 4 / 10], serial: 0)); // 100 ms
        Thread.Sleep(250);

        Assert.InRange(sink.PlayedSeconds, 0.09, 0.101);
    }

    [Fact]
    public void SilentAudioSink_ClockReadingIsAlwaysValid()
    {
        using var sink = new SilentAudioSink();
        sink.Open(48000, 2);
        sink.Resume();

        Assert.True(sink.Write(new byte[4800], serial: 0));
        Assert.True(sink.TryGetPlayedSeconds(out var playedSeconds));
        Assert.InRange(playedSeconds, 0, 0.0251);
    }

    [Fact]
    public void SilentAudioSink_Reset_AbortsBlockedWrite()
    {
        using var sink = new SilentAudioSink();
        sink.Open(48000, 2);
        sink.Pause(); // clock stopped: nothing drains, so the second write must block
        Assert.True(sink.Write(new byte[48000 * 4 / 10], serial: 0)); // 100 ms fills the lead

        var result = true;
        var writer = new Thread(() => { result = sink.Write(new byte[48000 * 4], serial: 0); });
        writer.Start();
        Thread.Sleep(100);
        Assert.True(writer.IsAlive);

        sink.Reset(serial: 1);
        Assert.True(writer.Join(2000));
        Assert.False(result);
    }

    [Fact]
    public void SilentAudioSink_ResetRejectsOldSerialEvenWhenWriteStartsAfterReset()
    {
        using var sink = new SilentAudioSink();
        sink.Open(48000, 2);

        sink.Reset(serial: 1);

        Assert.False(sink.Write(new byte[480], serial: 0));
        Assert.True(sink.Write(new byte[480], serial: 1));
    }

    [Fact]
    public void AudioSinkResetFence_PublishesSerialOnlyAfterSuccessfulNativeReset()
    {
        Assert.Equal(7, AudioSinkResetFence.SerialAfterReset(7, succeeded: true));
        Assert.Equal(AudioSinkResetFence.RejectedSerial, AudioSinkResetFence.SerialAfterReset(7, succeeded: false));
    }

    [Fact]
    public void AudioWriteCanAnchor_RequiresAcceptedCurrentSerial()
    {
        Assert.True(FfmpegPlayer.AudioWriteCanAnchor(writeAccepted: true, serial: 4, currentSerial: 4));
        Assert.False(FfmpegPlayer.AudioWriteCanAnchor(writeAccepted: false, serial: 4, currentSerial: 4));
        Assert.False(FfmpegPlayer.AudioWriteCanAnchor(writeAccepted: true, serial: 4, currentSerial: 5));
    }

    [Theory]
    [InlineData(false, false, 4, 4, 4, true)]
    [InlineData(true, false, 4, 4, 4, false)]
    [InlineData(false, true, 4, 4, 4, false)]
    [InlineData(false, false, 4, 5, 5, false)]
    [InlineData(false, false, 4, 4, 5, false)]
    public void AudioWriteFailureIsDeviceFailure_RequiresRejectedCurrentSerialWithoutClose(
        bool writeAccepted,
        bool closing,
        int serial,
        int currentSerial,
        int requestedSerial,
        bool expected)
    {
        Assert.Equal(
            expected,
            FfmpegPlayer.AudioWriteFailureIsDeviceFailure(
                writeAccepted,
                closing,
                serial,
                currentSerial,
                requestedSerial));
    }

    [Fact]
    public void AudioSinkClockHealth_ToleratesOneTransientReadFailure()
    {
        var failures = AudioSinkClockHealth.NextFailureCount(0, readSucceeded: false);
        Assert.Equal(1, failures);
        Assert.True(AudioSinkClockHealth.IsHealthy(baselineValid: true, failures));

        failures = AudioSinkClockHealth.NextFailureCount(failures, readSucceeded: true);
        Assert.Equal(0, failures);
        Assert.True(AudioSinkClockHealth.IsHealthy(baselineValid: true, failures));
    }

    [Fact]
    public void AudioSinkClockHealth_TwoConsecutiveFailuresBecomeUnhealthy()
    {
        var failures = AudioSinkClockHealth.NextFailureCount(0, readSucceeded: false);
        failures = AudioSinkClockHealth.NextFailureCount(failures, readSucceeded: false);

        Assert.Equal(AudioSinkClockHealth.ConsecutiveFailuresBeforeUnhealthy, failures);
        Assert.False(AudioSinkClockHealth.IsHealthy(baselineValid: true, failures));
    }

    [Fact]
    public void AudioSinkClockHealth_InvalidBaselineStaysUnhealthy()
    {
        Assert.False(AudioSinkClockHealth.IsHealthy(baselineValid: false, consecutiveFailures: 0));
    }

    [Theory]
    [InlineData(false, false, 4, 4, 4, true)]
    [InlineData(true, false, 4, 4, 4, false)]
    [InlineData(false, true, 4, 4, 4, false)]
    [InlineData(false, false, 3, 4, 4, false)]
    [InlineData(false, false, 4, 4, 5, false)]
    public void AudioClockReadFailureIsDeviceFailure_RequiresInvalidCurrentLatestSerial(
        bool clockValid,
        bool closing,
        int audioAnchorSerial,
        int currentSerial,
        int requestedSerial,
        bool expected)
    {
        Assert.Equal(
            expected,
            FfmpegPlayer.AudioClockReadFailureIsDeviceFailure(
                clockValid,
                closing,
                audioAnchorSerial,
                currentSerial,
                requestedSerial));
    }

    [Fact]
    public void AudioClockFailoverPosition_PreservesLastPlayedAudioPosition()
    {
        Assert.Equal(
            12.5,
            FfmpegPlayer.AudioClockFailoverPosition(
                audioAnchorPts: 10,
                audioAnchorSerial: 7,
                serial: 7,
                playedSeconds: 1.25,
                audioSpeed: 2,
                wallClockPosition: 99),
            precision: 6);

        Assert.Equal(
            99,
            FfmpegPlayer.AudioClockFailoverPosition(
                audioAnchorPts: double.NaN,
                audioAnchorSerial: -1,
                serial: 7,
                playedSeconds: 1.25,
                audioSpeed: 2,
                wallClockPosition: 99),
            precision: 6);
    }

    [Fact]
    public void AudioQueueStartFence_FailsClosedOnAnyCoreAudioStartError()
    {
        Assert.False(AudioQueueStartFence.Failed(0));
        Assert.True(AudioQueueStartFence.Failed(-1));
        Assert.True(AudioQueueStartFence.Failed(1234));
    }

    [Fact]
    public void ExtractLibraries_TakesOnlyBinDlls_Flattened()
    {
        var folder = Path.Combine(Path.GetTempPath(), "se-ffmpeg-libs-" + Guid.NewGuid().ToString("N"));
        var zip = folder + ".zip";
        try
        {
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                AddEntry(archive, "ffmpeg-n9.0-latest-win64-lgpl-shared-9.0/bin/avcodec-63.dll");
                AddEntry(archive, "ffmpeg-n9.0-latest-win64-lgpl-shared-9.0/bin/ffmpeg.exe");
                AddEntry(archive, "ffmpeg-n9.0-latest-win64-lgpl-shared-9.0/lib/avcodec.lib");
                AddEntry(archive, "ffmpeg-n9.0-latest-win64-lgpl-shared-9.0/include/libavcodec/avcodec.h");
            }

            DownloadFfmpegLibsViewModel.ExtractLibraries(zip, folder, CancellationToken.None);

            var files = Directory.GetFiles(folder).Select(Path.GetFileName).ToArray();
            Assert.Single(files);
            Assert.Equal("avcodec-63.dll", files[0]);
        }
        finally
        {
            File.Delete(zip);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }

    [Fact]
    public void ExtractLibraries_NoDlls_Throws()
    {
        var folder = Path.Combine(Path.GetTempPath(), "se-ffmpeg-libs-" + Guid.NewGuid().ToString("N"));
        var zip = folder + ".zip";
        try
        {
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                AddEntry(archive, "x/bin/ffmpeg.exe");
            }

            Assert.Throws<InvalidOperationException>(() => DownloadFfmpegLibsViewModel.ExtractLibraries(zip, folder, CancellationToken.None));
        }
        finally
        {
            File.Delete(zip);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }

    [Fact]
    public void AvCodecFileName_CarriesTheBindingsMajorVersion()
    {
        Assert.Contains(FfmpegLibraries.AvCodecMajor.ToString(), FfmpegLibraries.AvCodecFileName);
    }

    [Theory]
    [InlineData(0.0, 0.0, 60.0, 60.0)]
    [InlineData(0.0, 5.0, 60.0, 65.0)]
    [InlineData(10.0, 15.0, 60.0, 65.0)]
    [InlineData(10.0, double.NaN, 60.0, 60.0)]
    [InlineData(10.0, 5.0, 2.0, 0.0)]
    public void StreamEndPosition_UsesStartOffsetAndDuration(
        double formatStart,
        double streamStart,
        double streamDuration,
        double expected)
    {
        Assert.Equal(expected, FfmpegPlayer.StreamEndPosition(formatStart, streamStart, streamDuration));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void StreamEndPosition_UnknownDurationStaysUnknown(double streamDuration)
    {
        Assert.True(double.IsNaN(FfmpegPlayer.StreamEndPosition(0, 5, streamDuration)));
    }

    [Theory]
    [InlineData(120.0, 50.0, 65.0, 120.0)]
    [InlineData(120.0, double.NaN, double.NaN, 120.0)]
    [InlineData(0.0, 50.0, 65.0, 65.0)]
    [InlineData(double.NaN, 50.0, 65.0, 65.0)]
    [InlineData(0.0, 50.0, 0.0, 50.0)]
    [InlineData(0.0, 0.0, 0.0, 0.0)]
    [InlineData(0.0, 50.0, double.NaN, 0.0)]
    [InlineData(0.0, double.NaN, 65.0, 0.0)]
    public void PlaybackDuration_PrefersContainerThenSelectedPlaybackStreams(
        double formatDuration,
        double videoEnd,
        double audioEnd,
        double expected)
    {
        Assert.Equal(expected, FfmpegPlayer.PlaybackDuration(formatDuration, videoEnd, audioEnd));
    }

    [Theory]
    [InlineData(60.0, 42.5, 60.0)]
    [InlineData(0.0, 42.5, 42.5)]
    [InlineData(double.NaN, 42.5, 42.5)]
    [InlineData(0.0, double.NaN, 0.0)]
    [InlineData(0.0, -1.0, 0.0)]
    public void EndPosition_KnownDurationOrObservedFallback(double duration, double observed, double expected)
    {
        Assert.Equal(expected, FfmpegPlayer.EndPosition(duration, observed));
    }

    [Theory]
    [InlineData(3, 3, 12.0, 11.994, false)]
    [InlineData(3, 3, 12.0, 11.995, true)]
    [InlineData(3, 3, 12.0, 12.0, true)]
    [InlineData(2, 3, 12.0, 99.0, false)]
    [InlineData(3, 3, double.NaN, 0.0, true)]
    public void AudioDrainComplete_RequiresCurrentEofAndPlayedTail(
        int eofSerial,
        int currentSerial,
        double audioEnd,
        double clock,
        bool expected)
    {
        Assert.Equal(expected, FfmpegPlayer.AudioDrainComplete(eofSerial, currentSerial, audioEnd, clock));
    }

    [Theory]
    [InlineData(0.0, 10.0, 1.0, 10.0)]
    [InlineData(0.0, 10.0, 2.0, 20.0)]
    [InlineData(5.0, 10.0, 0.5, 10.0)]
    public void WallClockPosition_AppliesRateOnlyToElapsedTime(
        double basePosition,
        double elapsedSeconds,
        double speed,
        double expected)
    {
        Assert.Equal(expected, FfmpegPlayer.WallClockPosition(basePosition, elapsedSeconds, speed));
    }

    [Fact]
    public void SpeedChange_MustCaptureWallClockPositionBeforeChangingRate()
    {
        // Ten seconds actually played at 1x is still ten seconds when the user switches to 2x.
        // Reading Clock only after mutating the rate would reinterpret those same ten elapsed
        // seconds at 2x and incorrectly seek to 20 s.
        var correctTarget = FfmpegPlayer.WallClockPosition(0, 10, 1);
        var wrongTargetIfRateChangesFirst = FfmpegPlayer.WallClockPosition(0, 10, 2);

        Assert.Equal(10, correctTarget);
        Assert.Equal(20, wrongTargetIfRateChangesFirst);
    }

    [Theory]
    [InlineData(false, false, 7, 7, false)]
    [InlineData(true, false, 7, 7, true)]
    [InlineData(false, true, 7, 7, true)]
    [InlineData(false, false, 7, 8, true)]
    public void ShouldInterruptOpen_StopsClosingDisposedOrStaleLoads(
        bool closing,
        bool ownerDisposed,
        int loadGeneration,
        int currentGeneration,
        bool expected)
    {
        Assert.Equal(
            expected,
            FfmpegPlayer.ShouldInterruptOpen(closing, ownerDisposed, loadGeneration, currentGeneration));
    }

    [Fact]
    public void StaleSessionCannotOverwriteDecoderBadgeAfterClose()
    {
        using var player = new FfmpegPlayer();

        Assert.True(player.TrySetDecoderNameFromSession(0, "old-hardware"));
        Assert.Contains("old-hardware", player.Name);

        player.CloseFile(); // generation 0 -> 1 and clears owner-visible state

        Assert.False(player.TrySetDecoderNameFromSession(0, "stale-hardware"));
        Assert.Equal("ffmpeg", player.Name);
    }

    [Fact]
    public void StaleCloseCleanupCannotClearNewerGenerationMediaState()
    {
        using var player = new FfmpegPlayer();
        var queue = new VideoFrameQueue(1);
        var serial = 0;

        player.CloseFile(); // generation 0 -> 1

        Assert.True(player.TrySetDecoderNameFromSession(1, "current-hardware"));
        var frame = queue.Rent(4, 4, 0, ref serial)!;
        Assert.True(player.TryPresentFrameFromSession(1, frame, queue));
        var currentVersion = player.FrameVersion;

        Assert.False(player.TryClearOwnerMediaStateForGeneration(0));
        Assert.Contains("current-hardware", player.Name);
        Assert.Equal((4, 4), player.CurrentFrameSize);
        Assert.Equal(currentVersion, player.FrameVersion);

        Assert.True(player.TryClearOwnerMediaStateForGeneration(1));
        Assert.Equal("ffmpeg", player.Name);
        Assert.Equal((0, 0), player.CurrentFrameSize);
        Assert.Equal(currentVersion + 1, player.FrameVersion);

        queue.Close();
    }

    [Fact]
    public void StaleSessionFrameIsReturnedInsteadOfPublishedAfterClose()
    {
        using var player = new FfmpegPlayer();
        var queue = new VideoFrameQueue(1);
        var serial = 0;
        var frame = queue.Rent(4, 4, 0, ref serial)!;

        player.CloseFile(); // invalidate generation 0 before the stale worker publishes
        var versionAfterClose = player.FrameVersion;

        Assert.False(player.TryPresentFrameFromSession(0, frame, queue));
        Assert.Equal(versionAfterClose, player.FrameVersion);
        Assert.Equal((0, 0), player.CurrentFrameSize);

        // Rejection returns ownership to the originating session's pool instead of leaking the
        // frame or handing it to the new/current session.
        var reused = queue.Rent(4, 4, 0, ref serial);
        Assert.Same(frame, reused);
        queue.Return(reused);
        queue.Close();
    }

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(true, 2, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 0, true)]
    public void ShouldScheduleDeferredSessionCleanup_RequiresDeferredLastWorkerExit(
        bool cleanupDeferred,
        int activeWorkers,
        bool expected)
    {
        Assert.Equal(
            expected,
            FfmpegPlayer.ShouldScheduleDeferredSessionCleanup(cleanupDeferred, activeWorkers));
    }

    [Theory]
    [InlineData(12.5, 60.0, 12.5)]
    [InlineData(75.0, 60.0, 60.0)] // past the end: clamped to the duration
    [InlineData(0.0, 60.0, 0.0)]
    public void SeekTarget_KnownDuration_ClampsToIt(double value, double duration, double expected)
    {
        Assert.Equal(expected, FfmpegPlayer.SeekTarget(value, duration));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void SeekTarget_UnknownDuration_SeeksToTheValueUnclamped(double duration)
    {
        // Raw .h264 elementary streams and some transport streams report no duration; clamping to
        // it turned every seek into Seek(0).
        Assert.Equal(42.25, FfmpegPlayer.SeekTarget(42.25, duration));
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(double.NaN)]
    public void SeekTarget_InvalidValue_DoesNotSeek(double value)
    {
        Assert.Null(FfmpegPlayer.SeekTarget(value, 60));
    }

    [Fact]
    public void FailedSeekState_LatestRequestRollsBackToCommittedPipeline()
    {
        var state = FfmpegPlayer.FailedSeekState(
            failedSerial: 4,
            currentSerial: 3,
            requestedSerial: 4,
            requestedTarget: 42.0,
            currentPosition: 12.5);

        Assert.Equal(3, state.RequestedSerial);
        Assert.Equal(12.5, state.RequestedTarget);
    }

    [Fact]
    public void FailedSeekState_StaleFailureDoesNotEraseNewerRequest()
    {
        var state = FfmpegPlayer.FailedSeekState(
            failedSerial: 4,
            currentSerial: 3,
            requestedSerial: 5,
            requestedTarget: 55.0,
            currentPosition: 12.5);

        Assert.Equal(5, state.RequestedSerial);
        Assert.Equal(55.0, state.RequestedTarget);
    }

    [Theory]
    [InlineData(4, 4, 7, -1)]
    [InlineData(4, 5, 9, 9)]
    public void FailedSeekAudioStreamState_RollsBackOnlyTheFailedLatestRequest(
        int failedSerial,
        int requestedSerial,
        int requestedAudioStreamIndex,
        int expected)
    {
        Assert.Equal(expected, FfmpegPlayer.FailedSeekAudioStreamState(
            failedSerial,
            requestedSerial,
            requestedAudioStreamIndex));
    }

    [Fact]
    public void NextAudioStreamIndex_UsesPendingSelectionForRapidToggles()
    {
        var streams = new[] { 2, 5, 8 };

        Assert.Equal(8, FfmpegPlayer.NextAudioStreamIndex(streams, committedAudioStreamIndex: 5, requestedAudioStreamIndex: -1));
        Assert.Equal(2, FfmpegPlayer.NextAudioStreamIndex(streams, committedAudioStreamIndex: 5, requestedAudioStreamIndex: 8));
    }

    [Theory]
    [InlineData(false, true, 60.0, 60.0, true)]
    [InlineData(false, false, 60.0, 60.0, true)]
    [InlineData(false, false, 0.0, 60.0, false)]
    [InlineData(true, true, 60.0, 60.0, false)]
    [InlineData(true, false, 60.0, 60.0, false)]
    public void ShouldAutoRewindOnPlay_DoesNotOverwriteOutstandingSeek(
        bool hasOutstandingSeek,
        bool endReached,
        double duration,
        double position,
        bool expected)
    {
        Assert.Equal(expected, FfmpegPlayer.ShouldAutoRewindOnPlay(
            hasOutstandingSeek,
            endReached,
            duration,
            position));
    }

    [Theory]
    [InlineData(true, false, 60.0, 60.0, true)]
    [InlineData(true, true, 60.0, 60.0, false)]
    [InlineData(false, false, 60.0, 60.0, false)]
    [InlineData(true, false, 0.0, 60.0, false)]
    public void ShouldReachAudioOnlyEnd_WaitsForOutstandingSeek(
        bool playing,
        bool hasOutstandingSeek,
        double duration,
        double position,
        bool expected)
    {
        Assert.Equal(expected, FfmpegPlayer.ShouldReachAudioOnlyEnd(
            playing,
            hasOutstandingSeek,
            duration,
            position));
    }

    [Fact]
    public void ShouldResendPacket_RequiresRejectedInputDecoderProgressAndCurrentSerial()
    {
        Assert.True(FfmpegPlayer.ShouldResendPacket(-ffmpeg.EAGAIN, receivedOutput: true, interrupted: false));
        Assert.False(FfmpegPlayer.ShouldResendPacket(-ffmpeg.EAGAIN, receivedOutput: false, interrupted: false));
        Assert.False(FfmpegPlayer.ShouldResendPacket(-ffmpeg.EAGAIN, receivedOutput: true, interrupted: true));
        Assert.False(FfmpegPlayer.ShouldResendPacket(0, receivedOutput: true, interrupted: false));
        Assert.False(FfmpegPlayer.ShouldResendPacket(ffmpeg.AVERROR_EOF, receivedOutput: true, interrupted: false));
    }

    [Fact]
    public void WaveOutPositionCounterToBytes_AcceptsDriverFallbackFormats()
    {
        Assert.Equal(1234, WaveOutPosition.CounterToBytes(WaveOutPosition.TimeBytes, 1234, blockAlign: 4, bytesPerSecond: 192000));
        Assert.Equal(4000, WaveOutPosition.CounterToBytes(WaveOutPosition.TimeSamples, 1000, blockAlign: 4, bytesPerSecond: 192000));
        Assert.Equal(176400, WaveOutPosition.CounterToBytes(WaveOutPosition.TimeMilliseconds, 1000, blockAlign: 4, bytesPerSecond: 176400));
        Assert.Null(WaveOutPosition.CounterToBytes(0x40, 1000, blockAlign: 4, bytesPerSecond: 192000));
    }

    [Fact]
    public void WaveOutPositionCounterWrapBytes_UsesTheReturnedCounterUnits()
    {
        const long span = 1L << 32;

        Assert.Equal(span, WaveOutPosition.CounterWrapBytes(WaveOutPosition.TimeBytes, blockAlign: 4, bytesPerSecond: 192000));
        Assert.Equal(span * 4, WaveOutPosition.CounterWrapBytes(WaveOutPosition.TimeSamples, blockAlign: 4, bytesPerSecond: 192000));
        Assert.Equal(span * 176400 / 1000, WaveOutPosition.CounterWrapBytes(WaveOutPosition.TimeMilliseconds, blockAlign: 4, bytesPerSecond: 176400));
    }

    [Fact]
    public void WaveOutPositionWrapBase_FormatSwitchRoundingDoesNotInventAFullWrap()
    {
        var wrap = WaveOutPosition.CounterWrapBytes(
            WaveOutPosition.TimeMilliseconds,
            blockAlign: 4,
            bytesPerSecond: 192000);

        Assert.Equal(0, WaveOutPosition.WrapBaseAfterFormatChange(
            convertedBytes: 3838,
            lastPositionBytes: 4000,
            wrapBytes: wrap));

        Assert.Equal(wrap, WaveOutPosition.WrapBaseAfterFormatChange(
            convertedBytes: 4000,
            lastPositionBytes: wrap + 4000,
            wrapBytes: wrap));
    }

    [Fact]
    public void IsDemuxEndOfInput_DistinguishesRecoverableReadErrors()
    {
        Assert.True(FfmpegPlayer.IsDemuxEndOfInput(ffmpeg.AVERROR_EOF, avioEnded: false));
        Assert.True(FfmpegPlayer.IsDemuxEndOfInput(ffmpeg.AVERROR_INVALIDDATA, avioEnded: true));
        Assert.False(FfmpegPlayer.IsDemuxEndOfInput(ffmpeg.AVERROR_INVALIDDATA, avioEnded: false));
        Assert.False(FfmpegPlayer.IsDemuxEndOfInput(-ffmpeg.EAGAIN, avioEnded: false));
    }

    [Fact]
    public void ShouldRetryDecoderOpenInSoftware_OnlyAfterAttachedHardwareFailure()
    {
        Assert.True(FfmpegPlayer.ShouldRetryDecoderOpenInSoftware(-1234, hardwareRequested: true, hardwareAttached: true));
        Assert.False(FfmpegPlayer.ShouldRetryDecoderOpenInSoftware(-1234, hardwareRequested: false, hardwareAttached: true));
        Assert.False(FfmpegPlayer.ShouldRetryDecoderOpenInSoftware(-1234, hardwareRequested: true, hardwareAttached: false));
        Assert.False(FfmpegPlayer.ShouldRetryDecoderOpenInSoftware(0, hardwareRequested: true, hardwareAttached: true));
    }

    [Fact]
    public void ShouldReplayHardwareSendFailure_OnlyForFatalHardwareErrors()
    {
        Assert.True(FfmpegPlayer.ShouldReplayHardwareSendFailure(-1234, hardware: true));
        Assert.False(FfmpegPlayer.ShouldReplayHardwareSendFailure(-1234, hardware: false));
        Assert.False(FfmpegPlayer.ShouldReplayHardwareSendFailure(-ffmpeg.EAGAIN, hardware: true));
        Assert.False(FfmpegPlayer.ShouldReplayHardwareSendFailure(ffmpeg.AVERROR_EOF, hardware: true));
        Assert.False(FfmpegPlayer.ShouldReplayHardwareSendFailure(0, hardware: true));
    }

    [Theory]
    [InlineData(6, -1, false)]
    [InlineData(6, 7, true)]
    [InlineData(7, 7, false)]
    [InlineData(8, 7, false)]
    public void ShouldDropPacketBeforeHardwareReplay_RequiresOlderSerial(
        int packetSerial,
        int minimumReplaySerial,
        bool expected)
    {
        Assert.Equal(
            expected,
            FfmpegPlayer.ShouldDropPacketBeforeHardwareReplay(packetSerial, minimumReplaySerial));
    }

    [Theory]
    [InlineData(AVSampleFormat.AV_SAMPLE_FMT_U8, false)]
    [InlineData(AVSampleFormat.AV_SAMPLE_FMT_S16, false)]
    [InlineData(AVSampleFormat.AV_SAMPLE_FMT_S32, false)]
    [InlineData(AVSampleFormat.AV_SAMPLE_FMT_FLT, false)]
    [InlineData(AVSampleFormat.AV_SAMPLE_FMT_DBL, false)]
    [InlineData(AVSampleFormat.AV_SAMPLE_FMT_S64, false)] // packed, but numbered after the first planar formats
    [InlineData(AVSampleFormat.AV_SAMPLE_FMT_U8P, true)]
    [InlineData(AVSampleFormat.AV_SAMPLE_FMT_S16P, true)]
    [InlineData(AVSampleFormat.AV_SAMPLE_FMT_S32P, true)]
    [InlineData(AVSampleFormat.AV_SAMPLE_FMT_FLTP, true)]
    [InlineData(AVSampleFormat.AV_SAMPLE_FMT_DBLP, true)]
    [InlineData(AVSampleFormat.AV_SAMPLE_FMT_S64P, true)]
    [InlineData(AVSampleFormat.AV_SAMPLE_FMT_NONE, false)]
    public void IsPlanarSampleFormat_MatchesLibavutilTable(AVSampleFormat format, bool planar)
    {
        Assert.Equal(planar, FfmpegPlayer.IsPlanarSampleFormat(format));
    }

    [Fact]
    public void IsPlanarSampleFormat_S64IsNotOrderedAfterAllPackedFormats()
    {
        // The bug: "format >= U8P" as the planar test. S64 sits between the planar formats.
        Assert.True(AVSampleFormat.AV_SAMPLE_FMT_S64 > AVSampleFormat.AV_SAMPLE_FMT_U8P);
        Assert.False(FfmpegPlayer.IsPlanarSampleFormat(AVSampleFormat.AV_SAMPLE_FMT_S64));
    }

    private static void AddEntry(ZipArchive archive, string name)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(new byte[] { 1, 2, 3 });
    }
}
