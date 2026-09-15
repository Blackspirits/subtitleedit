using Nikse.SubtitleEdit.Logic.VideoPlayers.Ffmpeg;
using Nikse.SubtitleEdit.UiLogic;
using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Logic.Download;

public interface IFfmpegLibsDownloadService
{
    Task DownloadFfmpegLibs(string destinationFileName, IProgress<float>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Downloads the FFmpeg shared libraries (avcodec/avformat/... DLLs) the ffmpeg video player
/// needs. The static ffmpeg.exe Subtitle Edit already downloads contains no DLLs, so this is a
/// separate package: the LGPL shared build of the release line the bindings are generated for
/// (<see cref="FfmpegLibraries.MajorVersion"/>) from BtbN's FFmpeg-Builds.
/// </summary>
public class FfmpegLibsDownloadService(HttpClient httpClient) : IFfmpegLibsDownloadService
{
    internal const string WindowsX64ReleaseTag = "autobuild-2026-09-15-13-18";
    internal const string WindowsX64AssetName = "ffmpeg-n9.0.1-30-g9258bacca5-win64-lgpl-shared-9.0.zip";
    internal const string WindowsX64Sha256 = "04d256aa477122949304a717bf61d8eec78e255c2c7c9be17fab1677227cb548";
    internal const string WindowsX64Url =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/" + WindowsX64ReleaseTag + "/" + WindowsX64AssetName;

    public async Task DownloadFfmpegLibs(string destinationFileName, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        await DownloadHelper.DownloadFileAsync(httpClient, GetUrl(), destinationFileName, progress, cancellationToken);
        await VerifySha256Async(destinationFileName, WindowsX64Sha256, cancellationToken);
    }

    internal static async Task VerifySha256Async(string filePath, string expectedSha256, CancellationToken cancellationToken)
    {
        var actual = await Sha256Util.ComputeSha256Async(filePath, cancellationToken);
        if (string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        TryDeleteFile(filePath);
        throw new InvalidOperationException(
            $"Downloaded FFmpeg library archive failed SHA-256 verification — expected {expectedSha256}, got {actual ?? "<missing>"}. The file has been removed.");
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Best effort: never treat a failed cleanup as successful verification.
        }
    }

    private static string GetUrl()
    {
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return WindowsX64Url;
        }

        throw new PlatformNotSupportedException("FFmpeg shared library download is only available for Windows x64; install FFmpeg from your package manager instead.");
    }
}
