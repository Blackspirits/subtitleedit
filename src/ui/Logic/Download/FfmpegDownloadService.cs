using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nikse.SubtitleEdit.UiLogic;

namespace Nikse.SubtitleEdit.Logic.Download;

public interface IFfmpegDownloadService
{
    Task DownloadFfmpeg(string destinationFileName, IProgress<float>? progress, CancellationToken cancellationToken);
    Task DownloadFfmpeg(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken);
}

public class FfmpegDownloadService : IFfmpegDownloadService
{
    private readonly HttpClient _httpClient;
    private const string WindowsUrl = "https://github.com/SubtitleEdit/support-files/releases/download/ffmpeg-v9-1/ffmpeg901.zip";

    // Intel is still 8.0: osxexperts.net, where both macOS builds come from, has not published an
    // Intel build past 8.0.
    private const string MacUrl = "https://github.com/SubtitleEdit/support-files/releases/download/ffmpeg-v8/ffmpeg80intel.zip";
    private const string MacUrlArm = "https://github.com/SubtitleEdit/support-files/releases/download/ffmpeg-v9-1/ffmpeg90arm.zip";

    // GitHub-published release-asset digests for the exact archives above. Keep this map in sync
    // with the pinned URLs so a future URL bump cannot silently disable integrity verification.
    internal static readonly IReadOnlyDictionary<string, string> KnownSha256 =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ffmpeg901.zip"] = "89575634e89298191693e74d97f2a01fdb251bdfe95f4cb64f8eaa9883da9844",
            ["ffmpeg80intel.zip"] = "439c92ccbc6cf3116c4713d1724c3765f4fc68ad2351be6fa5709d7b52e1f063",
            ["ffmpeg90arm.zip"] = "21721909d4a24544359aff1ac5ce0dded8a947a2abc4c939c8d525a7c6cc881b",
        };

    public FfmpegDownloadService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    private static string GetFfmpegUrl()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsUrl;
        }

        if (OperatingSystem.IsMacOS())
        {
            switch (RuntimeInformation.ProcessArchitecture)
            {
                case Architecture.Arm64:
                    return MacUrlArm; // e.g., for M1, M2, M3, M4 chips
                case Architecture.X64:
                    return MacUrl;
                default:
                    throw new PlatformNotSupportedException("Unsupported macOS architecture.");
            }
        }

        throw new PlatformNotSupportedException();
    }

    internal static string GetExpectedSha256(string url)
    {
        var assetName = Path.GetFileName(new Uri(url).AbsolutePath);
        if (!KnownSha256.TryGetValue(assetName, out var expectedSha256))
        {
            throw new InvalidOperationException($"No SHA-256 is pinned for FFmpeg asset '{assetName}'.");
        }

        return expectedSha256;
    }

    internal static async Task VerifyChecksumAsync(Stream stream, string expectedSha256, CancellationToken cancellationToken)
    {
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new InvalidOperationException("FFmpeg integrity verification requires a readable, seekable stream.");
        }

        string actualSha256;
        stream.Position = 0;
        try
        {
            actualSha256 = await Sha256Util.ComputeSha256Async(stream, cancellationToken);
        }
        finally
        {
            stream.Position = 0;
        }

        if (!string.Equals(expectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"FFmpeg download failed integrity check (expected SHA-256 {expectedSha256}, got {actualSha256}).");
        }
    }

    internal async Task DownloadAndVerifyAsync(
        Stream stream,
        string url,
        string expectedSha256,
        IProgress<float>? progress,
        CancellationToken cancellationToken)
    {
        await DownloadHelper.DownloadFileAsync(_httpClient, url, stream, progress, cancellationToken);
        await VerifyChecksumAsync(stream, expectedSha256, cancellationToken);
    }

    public async Task DownloadFfmpeg(string destinationFileName, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        var url = GetFfmpegUrl();
        var expectedSha256 = GetExpectedSha256(url);

        await DownloadHelper.DownloadFileAsync(_httpClient, url, destinationFileName, progress, cancellationToken);

        try
        {
            await using var stream = File.OpenRead(destinationFileName);
            await VerifyChecksumAsync(stream, expectedSha256, cancellationToken);
        }
        catch
        {
            try
            {
                File.Delete(destinationFileName);
            }
            catch
            {
                // Best effort: verification failure must remain the primary error.
            }

            throw;
        }
    }

    public async Task DownloadFfmpeg(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        var url = GetFfmpegUrl();
        var expectedSha256 = GetExpectedSha256(url);

        await DownloadAndVerifyAsync(stream, url, expectedSha256, progress, cancellationToken);
    }
}
