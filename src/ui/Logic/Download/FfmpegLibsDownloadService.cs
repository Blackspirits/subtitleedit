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
    // Pin one dated autobuild rather than BtbN's mutable "latest" alias. A future FFmpeg update
    // must deliberately update both this URL and its GitHub-published SHA-256 together.
    internal const string WindowsX64Url =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-09-11-13-20/ffmpeg-n9.0.1-29-gad500d59cb-win64-lgpl-shared-9.0.zip";
    internal const string WindowsX64Sha256 =
        "40eec25b2f55dcad7e4d4e640919b920d29818b56fdaf9353ce1fd8adefc9d6b";

    public async Task DownloadFfmpegLibs(string destinationFileName, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        var download = GetDownload();
        await DownloadAndVerifyAsync(httpClient, download.Url, download.Sha256, destinationFileName, progress, cancellationToken);
    }

    internal static async Task DownloadAndVerifyAsync(
        HttpClient client,
        string url,
        string expectedSha256,
        string destinationFileName,
        IProgress<float>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            throw new InvalidOperationException("No SHA-256 is registered for the FFmpeg shared-library archive.");
        }

        try
        {
            await DownloadHelper.DownloadFileAsync(client, url, destinationFileName, progress, cancellationToken);
            var actual = await Sha256Util.ComputeSha256Async(destinationFileName, cancellationToken);
            if (string.IsNullOrEmpty(actual) || !string.Equals(expectedSha256, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    $"FFmpeg shared-library download failed integrity check (expected SHA-256 {expectedSha256}, got {actual ?? "<missing>"}).");
            }
        }
        catch
        {
            TryDelete(destinationFileName);
            throw;
        }
    }

    private static (string Url, string Sha256) GetDownload()
    {
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return (WindowsX64Url, WindowsX64Sha256);
        }

        throw new PlatformNotSupportedException("FFmpeg shared library download is only available for Windows x64; install FFmpeg from your package manager instead.");
    }

    private static void TryDelete(string fileName)
    {
        try
        {
            if (File.Exists(fileName))
            {
                File.Delete(fileName);
            }
        }
        catch
        {
            // Best effort: preserve the original download/integrity failure.
        }
    }
}
