using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nikse.SubtitleEdit.UiLogic;

namespace Nikse.SubtitleEdit.Logic.Download;

public interface ILibVlcDownloadService
{
    Task DownloadLibVlc(string destinationFileName, IProgress<float>? progress, CancellationToken cancellationToken);

    Task DownloadLibVlc(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken);
}

public class LibVlcDownloadService(HttpClient httpClient) : ILibVlcDownloadService
{
    private const string WindowsX64Url = "https://get.videolan.org/vlc/3.0.23/win64/vlc-3.0.23-win64.7z";
    private const string WindowsX64Sha256 = "eb4fd8a28291da73608c733786a09610fea865fbe94113bcb60b91c1ebb8404a";
    private const string WindowsX86Url = "https://get.videolan.org/vlc/3.0.23/win32/vlc-3.0.23-win32.7z";
    private const string WindowsX86Sha256 = "f148ff49cdac6c0b6b7018ad7c4e6cd24c99bc6c2dea8258d82684261a639017";
    private const string MacX64Url = "https://github.com/SubtitleEdit/support-files/releases/download/vlc3/libvlc-osx64.7z";
    private const string MacX64Sha256 = "301c3c4a78ae2339d075f557af7ab0006c427dbd3e903c4778c59de9684c353a";

    internal readonly record struct DownloadInfo(string Url, string Sha256);

    public async Task DownloadLibVlc(string destinationFileName, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        await DownloadAndVerifyFileAsync(httpClient, GetDownload(), destinationFileName, progress, cancellationToken);
    }

    public async Task DownloadLibVlc(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken)
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
            throw new InvalidOperationException("No SHA-256 is registered for the selected libVLC archive.");
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
                    $"libVLC download failed integrity check (expected SHA-256 {download.Sha256}, got {actual}).");
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
            throw new InvalidOperationException("No SHA-256 is registered for the selected libVLC archive.");
        }

        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new InvalidOperationException("libVLC integrity verification requires a readable, seekable stream.");
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
                $"libVLC download failed integrity check (expected SHA-256 {expectedSha256}, got {actual}).");
        }
    }

    private static DownloadInfo GetDownload()
    {
        if (OperatingSystem.IsWindows())
        {
            return ResolveWindowsDownload(RuntimeInformation.ProcessArchitecture);
        }

        if (OperatingSystem.IsMacOS())
        {
            return ResolveMacDownload(RuntimeInformation.ProcessArchitecture);
        }

        throw new PlatformNotSupportedException("LibVLC download is not supported on this platform");
    }

    internal static DownloadInfo ResolveWindowsDownload(Architecture architecture)
    {
        return architecture switch
        {
            Architecture.X64 => new DownloadInfo(WindowsX64Url, WindowsX64Sha256),
            Architecture.X86 => new DownloadInfo(WindowsX86Url, WindowsX86Sha256),
            _ => throw new PlatformNotSupportedException("Unsupported Windows architecture."),
        };
    }

    internal static DownloadInfo ResolveMacDownload(Architecture architecture)
    {
        return architecture switch
        {
            // case Architecture.Arm64:
            //     return new DownloadInfo(MacArmUrl, MacArmSha256); // e.g. M1-M5
            Architecture.X64 => new DownloadInfo(MacX64Url, MacX64Sha256),
            _ => throw new PlatformNotSupportedException("Unsupported macOS architecture."),
        };
    }
}
