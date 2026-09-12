using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nikse.SubtitleEdit.UiLogic;

namespace Nikse.SubtitleEdit.Logic.Download;

public interface IGoogleLensOcrDownloadService
{
    Task DownloadGoogleLensOcrStandalone(string destinationFileName, IProgress<float>? progress, CancellationToken cancellationToken);

    Task DownloadGoogleLensOcrStandalone(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken);
}

public class GoogleLensOcrDownloadService(HttpClient httpClient) : IGoogleLensOcrDownloadService
{
    //private const string WindowsUrl = "https://github.com/timminator/chrome-lens-py/releases/download/v3.3.0/Chrome-Lens-CLI-v3.3.0.7z";
    private const string WindowsUrl = "https://github.com/timminator/Chrome-Lens-OCR/releases/download/v3.4.0/Chrome-Lens-OCR-v3.4.0.7z";
    private const string WindowsSha256 = "201685c3a3857515360174ab1e470c0f6d1e35fd90ece76deb74c277d17df085";
    private const string LinuxUrl = "https://github.com/timminator/Chrome-Lens-OCR/releases/download/v3.4.0/Chrome-Lens-OCR-v3.4.0-Linux.7z";
    private const string LinuxSha256 = "661348e20c12e4e43df061189bb90c46eff07ec25835532c52f6dcb1ce9d6d42";

    internal readonly record struct DownloadInfo(string Url, string Sha256);

    public async Task DownloadGoogleLensOcrStandalone(string destinationFileName, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        await DownloadAndVerifyFileAsync(httpClient, GetDownload(), destinationFileName, progress, cancellationToken);
    }

    public async Task DownloadGoogleLensOcrStandalone(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        var download = GetDownload();
        await DownloadHelper.DownloadFileAsync(httpClient, download.Url, stream, progress, cancellationToken);
        await VerifyStreamAsync(stream, download.Sha256, cancellationToken);
    }

    internal static async Task DownloadAndVerifyFileAsync(
        HttpClient client,
        DownloadInfo download,
        string destinationFileName,
        IProgress<float>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(download.Sha256))
        {
            throw new InvalidOperationException("No SHA-256 is registered for the selected Google Lens OCR archive.");
        }

        try
        {
            await DownloadHelper.DownloadFileAsync(
                client,
                download.Url,
                destinationFileName,
                progress,
                cancellationToken);

            await using var stream = File.OpenRead(destinationFileName);
            var actual = await Sha256Util.ComputeSha256Async(stream, cancellationToken);
            if (!string.Equals(download.Sha256, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    $"Google Lens OCR download failed integrity check (expected SHA-256 {download.Sha256}, got {actual}).");
            }
        }
        catch
        {
            try
            {
                if (File.Exists(destinationFileName))
                {
                    File.Delete(destinationFileName);
                }
            }
            catch
            {
                // Preserve the original download/integrity error; cleanup is best-effort.
            }

            throw;
        }
    }

    internal static async Task VerifyStreamAsync(
        Stream stream,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            throw new InvalidOperationException("No SHA-256 is registered for the selected Google Lens OCR archive.");
        }

        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new InvalidOperationException("Google Lens OCR integrity verification requires a readable, seekable stream.");
        }

        string actual;
        stream.Position = 0;
        try
        {
            actual = await Sha256Util.ComputeSha256Async(stream, cancellationToken);
        }
        finally
        {
            stream.Position = 0;
        }

        if (!string.Equals(expectedSha256, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"Google Lens OCR download failed integrity check (expected SHA-256 {expectedSha256}, got {actual}).");
        }
    }

    private static DownloadInfo GetDownload()
    {
        if (OperatingSystem.IsWindows())
        {
            return ResolveWindowsDownload();
        }

        if (OperatingSystem.IsLinux())
        {
            return ResolveLinuxDownload(RuntimeInformation.ProcessArchitecture);
        }

        throw new PlatformNotSupportedException("Google Lens OCR does not support this platform");
    }

    internal static DownloadInfo ResolveWindowsDownload()
    {
        return new DownloadInfo(WindowsUrl, WindowsSha256);
    }

    internal static DownloadInfo ResolveLinuxDownload(Architecture architecture)
    {
        if (architecture == Architecture.Arm64)
        {
            throw new PlatformNotSupportedException("Google Lens OCR is not available for Linux ARM64.");
        }

        return new DownloadInfo(LinuxUrl, LinuxSha256);
    }
}
