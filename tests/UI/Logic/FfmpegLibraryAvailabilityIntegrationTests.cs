using FFmpeg.AutoGen;
using Nikse.SubtitleEdit.Logic.VideoPlayers.Ffmpeg;

namespace UITests.Logic;

public class FfmpegLibraryAvailabilityIntegrationTests
{
    [Fact]
    public void RequiredLibraryVersionsMatch_RequiresEveryBindingMajor()
    {
        static uint V(int major) => (uint)major << 16;

        var codec = V(ffmpeg.LIBAVCODEC_VERSION_MAJOR);
        var format = V(ffmpeg.LIBAVFORMAT_VERSION_MAJOR);
        var util = V(ffmpeg.LIBAVUTIL_VERSION_MAJOR);
        var scale = V(ffmpeg.LIBSWSCALE_VERSION_MAJOR);
        var resample = V(ffmpeg.LIBSWRESAMPLE_VERSION_MAJOR);

        Assert.True(FfmpegLibraries.RequiredLibraryVersionsMatch(codec, format, util, scale, resample));
        Assert.False(FfmpegLibraries.RequiredLibraryVersionsMatch(codec + (1u << 16), format, util, scale, resample));
        Assert.False(FfmpegLibraries.RequiredLibraryVersionsMatch(codec, format + (1u << 16), util, scale, resample));
        Assert.False(FfmpegLibraries.RequiredLibraryVersionsMatch(codec, format, util + (1u << 16), scale, resample));
        Assert.False(FfmpegLibraries.RequiredLibraryVersionsMatch(codec, format, util, scale + (1u << 16), resample));
        Assert.False(FfmpegLibraries.RequiredLibraryVersionsMatch(codec, format, util, scale, resample + (1u << 16)));
    }

    [Fact]
    public void VersionMajor_UsesFfmpegVersionEncoding()
    {
        Assert.Equal(63, FfmpegLibraries.VersionMajor((63u << 16) | (12u << 8) | 100u));
    }
}
