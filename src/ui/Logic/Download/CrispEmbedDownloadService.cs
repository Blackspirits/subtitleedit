using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nikse.SubtitleEdit.UiLogic;

namespace Nikse.SubtitleEdit.Logic.Download;

public interface ICrispEmbedDownloadService
{
    Task DownloadEngine(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken);
    Task DownloadEngineWindowsCuda(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken);
    Task DownloadEngineWindowsVulkan(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken);
    Task DownloadEngineWindowsCpu(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken);
    Task DownloadEngineLinuxCuda(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken);
    Task DownloadModel(string url, string destinationFileName, IProgress<float>? progress, CancellationToken cancellationToken);
}

public class CrispEmbedDownloadService : ICrispEmbedDownloadService
{
    private readonly HttpClient _httpClient;

    private const string WindowsCudaUrl = "https://github.com/CrispStrobe/CrispEmbed/releases/download/v0.17.9/crispembed-windows-x86_64-cuda.zip";
    private const string WindowsVulkanUrl = "https://github.com/CrispStrobe/CrispEmbed/releases/download/v0.17.9/crispembed-windows-x86_64-vulkan.zip";
    private const string WindowsCpuUrl = "https://github.com/CrispStrobe/CrispEmbed/releases/download/v0.17.9/crispembed-windows-x86_64.zip";
    private const string MacUrl = "https://github.com/CrispStrobe/CrispEmbed/releases/download/v0.17.9/crispembed-macos-arm64.tar.gz";
    private const string LinuxUrl = "https://github.com/CrispStrobe/CrispEmbed/releases/download/v0.17.9/crispembed-linux-x86_64.tar.gz";
    // The plain "-cuda" archive expects a CUDA 12.x *toolkit* on the host (libcudart/libcublas),
    // not just a driver, and fails in the dynamic loader with exit code 127 when it is missing.
    // The "-bundled" variant ships those two libraries, so it runs with only an NVIDIA driver.
    // Note the CUDA archives keep a glibc 2.38 floor (they build outside the manylinux container
    // the CPU archives use, which lowered those to 2.27).
    private const string LinuxCudaUrl = "https://github.com/CrispStrobe/CrispEmbed/releases/download/v0.17.9/crispembed-linux-x86_64-cuda-bundled.tar.gz";
    private const string LinuxArmUrl = "https://github.com/CrispStrobe/CrispEmbed/releases/download/v0.17.9/crispembed-linux-arm64.tar.gz";

    public CrispEmbedDownloadService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task DownloadEngine(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        var download = GetDefaultDownload();
        await DownloadAndVerifyAsync(download.Url, download.HashKey, stream, progress, cancellationToken);
    }

    public async Task DownloadEngineWindowsCuda(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        await DownloadAndVerifyAsync(WindowsCudaUrl, DownloadHashManager.CrispEmbed.WindowsCuda, stream, progress, cancellationToken);
    }

    public async Task DownloadEngineWindowsVulkan(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        await DownloadAndVerifyAsync(WindowsVulkanUrl, DownloadHashManager.CrispEmbed.WindowsVulkan, stream, progress, cancellationToken);
    }

    public async Task DownloadEngineWindowsCpu(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        await DownloadAndVerifyAsync(WindowsCpuUrl, DownloadHashManager.CrispEmbed.WindowsCpu, stream, progress, cancellationToken);
    }

    public async Task DownloadEngineLinuxCuda(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        await DownloadAndVerifyAsync(LinuxCudaUrl, DownloadHashManager.CrispEmbed.LinuxCuda, stream, progress, cancellationToken);
    }

    public async Task DownloadModel(string url, string destinationFileName, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        await DownloadHelper.DownloadFileAsync(_httpClient, url, destinationFileName, progress, cancellationToken);
    }

    private async Task DownloadAndVerifyAsync(
        string url,
        string hashKey,
        Stream stream,
        IProgress<float>? progress,
        CancellationToken cancellationToken)
    {
        await DownloadHelper.DownloadFileAsync(_httpClient, url, stream, progress, cancellationToken);
        await VerifyArchiveAsync(stream, hashKey, cancellationToken);
    }

    internal static async Task VerifyArchiveAsync(Stream stream, string? hashKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(hashKey))
        {
            throw new InvalidOperationException("No SHA-256 key is registered for the CrispEmbed runtime.");
        }

        var expected = DownloadHashManager.GetLatestKnownHash(hashKey);
        if (string.IsNullOrEmpty(expected))
        {
            throw new InvalidOperationException($"No SHA-256 is registered for CrispEmbed runtime key '{hashKey}'.");
        }

        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new InvalidOperationException("CrispEmbed runtime integrity verification requires a readable, seekable stream.");
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

        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"CrispEmbed runtime download failed integrity check (expected SHA-256 {expected}, got {actual}).");
        }
    }

    private static (string Url, string HashKey) GetDefaultDownload()
    {
        if (OperatingSystem.IsWindows())
        {
            return (WindowsVulkanUrl, DownloadHashManager.CrispEmbed.WindowsVulkan);
        }

        if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? (LinuxArmUrl, DownloadHashManager.CrispEmbed.LinuxArm)
                : (LinuxUrl, DownloadHashManager.CrispEmbed.Linux);
        }

        if (OperatingSystem.IsMacOS())
        {
            return (MacUrl, DownloadHashManager.CrispEmbed.MacOs);
        }

        throw new PlatformNotSupportedException();
    }
}
