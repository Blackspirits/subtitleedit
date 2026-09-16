using System;

namespace Nikse.SubtitleEdit.Logic.VideoPlayers.Ffmpeg.Audio;

/// <summary>
/// Where decoded PCM goes. Interleaved signed 16-bit samples, the sample rate and channel count
/// given to <see cref="Open"/>. The sink is also the player's master clock while audio plays:
/// <see cref="PlayedSeconds"/> is how much of the audio written since the last <see cref="Reset"/>
/// has actually come out of the speaker, so video frames are shown against real audio time
/// rather than against a CPU timer that drifts from the sound card.
/// </summary>
public interface IAudioSink : IDisposable
{
    void Open(int sampleRate, int channels);

    /// <summary>
    /// Queue PCM for playback under the given seek serial. Blocks while the device queue is full -
    /// that back pressure is what paces the audio decoder. Returns false when the serial is no
    /// longer current, or when the sink was reset/closed while waiting, so stale PCM can never be
    /// queued after a seek.
    /// </summary>
    bool Write(ReadOnlySpan<byte> pcm, int serial);

    /// <summary>Seconds of audio played since the last <see cref="Reset"/>.</summary>
    double PlayedSeconds { get; }

    /// <summary>
    /// Returns the best known played position and whether the native clock read is currently
    /// trustworthy. A false result still supplies the last known position so the player can
    /// switch clocks without a discontinuity.
    /// </summary>
    bool TryGetPlayedSeconds(out double playedSeconds);

    /// <summary>
    /// Drop everything queued, make <paramref name="serial"/> the only serial accepted by
    /// <see cref="Write"/>, and restart the played counter at zero (seek, stop).
    /// </summary>
    void Reset(int serial);

    void Pause();
    void Resume();
}

internal static class AudioSinkResetFence
{
    internal const int RejectedSerial = int.MinValue;

    internal static int SerialAfterReset(int requestedSerial, bool succeeded)
    {
        return succeeded ? requestedSerial : RejectedSerial;
    }
}


internal static class AudioSinkClockHealth
{
    internal const int ConsecutiveFailuresBeforeUnhealthy = 2;

    internal static int NextFailureCount(int currentFailures, bool readSucceeded)
    {
        return readSucceeded
            ? 0
            : Math.Min(ConsecutiveFailuresBeforeUnhealthy, currentFailures + 1);
    }

    internal static bool IsHealthy(bool baselineValid, int consecutiveFailures)
    {
        return baselineValid && consecutiveFailures < ConsecutiveFailuresBeforeUnhealthy;
    }
}
