using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nikse.SubtitleEdit.UiLogic;

namespace Nikse.SubtitleEdit.Logic.Download;

public interface ILibMpvDownloadService
{
    Task DownloadLibMpv(string destinationFileName, IProgress<float>? progress, CancellationToken cancellationToken);

    Task DownloadLibMpv(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken);
}

public class LibMpvDownloadService : ILibMpvDownloadService
{
    private readonly HttpClient _httpClient;
    // The "b" repack bundles the Khronos Vulkan loader: this libmpv has a load-time
    // import of vulkan-1.dll, which GPU drivers older than Vulkan never installed (#13856).
    private const string WindowsUrl = "https://github.com/SubtitleEdit/support-files/releases/download/libmpv-2026-08-14b/libmpv2-win64.zip";
    private const string WindowsUrlArm = "https://github.com/SubtitleEdit/support-files/releases/download/libmpv-2026-08-14b/libmpv2-win-arm64.zip";
    private const string MacUrl = "";
    private const string MacUrlArm = "";

    // GitHub-published release-asset digests for the exact archives above. Keep this map in sync
    // with the pinned URLs so a future asset bump cannot silently disable integrity verification.
    internal static readonly IReadOnlyDictionary<string, string> KnownSha256 =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["libmpv2-win64.zip"] = "ce99ee7a9cab0ada2f696b04132def67b0978157d5f4a1a7966d04c92aebbfec",
            ["libmpv2-win-arm64.zip"] = "d8be93f69eb102026ba81d5d237887b858701510c9e5e26996a2d30f9829df00",
        };

    public LibMpvDownloadService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    private static string GetUrl()
    {
        if (OperatingSystem.IsWindows())
        {
            // A native ARM64 process cannot load an x64 DLL, so the download
            // must match the process architecture (issue #12087).
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? WindowsUrlArm
                : WindowsUrl;
        }

        throw new PlatformNotSupportedException("Unsupported platform for libmpv download." + Environment.NewLine +
            RuntimeInformation.OSDescription);

        //if (OperatingSystem.IsMacOS())
        //{
        //    switch (RuntimeInformation.ProcessArchitecture)
        //    {
        //        case Architecture.Arm64:
        //            return MacUrlArm; // e.g., for M1, M2, M3, M4 chips
        //        case Architecture.X64:
        //            return MacUrl;
        //        default:
        //            throw new PlatformNotSupportedException("Unsupported macOS architecture.");
        //    }
        //}

        //throw new PlatformNotSupportedException();
    }

    internal static string GetExpectedSha256(string url)
    {
        var assetName = Path.GetFileName(new Uri(url).AbsolutePath);
        if (!KnownSha256.TryGetValue(assetName, out var expectedSha256))
        {
            throw new InvalidOperationException($"No SHA-256 is pinned for libmpv asset '{assetName}'.");
        }

        return expectedSha256;
    }

    internal static async Task VerifyChecksumAsync(Stream stream, string expectedSha256, CancellationToken cancellationToken)
    {
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new InvalidOperationException("libmpv integrity verification requires a readable, seekable stream.");
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
                $"libmpv download failed integrity check (expected SHA-256 {expectedSha256}, got {actualSha256}).");
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

    public async Task DownloadLibMpv(string destinationFileName, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        var url = GetUrl();
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

    public async Task DownloadLibMpv(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        var url = GetUrl();
        var expectedSha256 = GetExpectedSha256(url);

        await DownloadAndVerifyAsync(stream, url, expectedSha256, progress, cancellationToken);
    }
}
