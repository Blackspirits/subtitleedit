using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nikse.SubtitleEdit.UiLogic;

namespace Nikse.SubtitleEdit.Logic.Download;

public interface ICrispAsrDownloadService
{
    Task DownloadEngine(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken);
    Task DownloadEngineWindowsCuda(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken);
    Task DownloadEngineWindowsCuda13(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken);
    Task DownloadEngineWindowsVulkan(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken);
    Task DownloadEngineWindowsCpu(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken);
    Task DownloadEngineWindowsCpuLegacy(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken);
    Task DownloadEngineLinuxCuda(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken);
    Task DownloadEngineLinuxCuda13(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken);
    Task DownloadEngineLinuxVulkan(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken);
    Task DownloadEngineLinuxHip(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken);
}

public class CrispAsrDownloadService : ICrispAsrDownloadService
{
    private readonly HttpClient _httpClient;

    private const string WindowsCudaUrl = "https://github.com/CrispStrobe/CrispASR/releases/download/v0.8.32/crispasr-windows-x86_64-cuda.zip";
    /// <summary>
    /// The CUDA 13 build, added upstream in v0.8.31 next to the CUDA 12 one rather than
    /// replacing it. Offered as its own option because CUDA 13 needs a newer NVIDIA driver than
    /// CUDA 12 - repointing <see cref="WindowsCudaUrl"/> at it would have broken everyone still
    /// on an older driver. Mirrors the Linux pair, which has had both since v0.8.30.
    /// </summary>
    private const string WindowsCuda13Url = "https://github.com/CrispStrobe/CrispASR/releases/download/v0.8.32/crispasr-windows-x86_64-cuda13.zip";
    private const string WindowsVulkanUrl = "https://github.com/CrispStrobe/CrispASR/releases/download/v0.8.32/crispasr-windows-x86_64-vulkan.zip";
    private const string WindowsCpuUrl = "https://github.com/CrispStrobe/CrispASR/releases/download/v0.8.32/crispasr-windows-x86_64-cpu.zip";
    private const string WindowsCpuLegacyUrl = "https://github.com/CrispStrobe/CrispASR/releases/download/v0.8.32/crispasr-windows-x86_64-cpu-legacy.zip";
    private const string MacUrl = "https://github.com/CrispStrobe/CrispASR/releases/download/v0.8.32/crispasr-macos.tar.gz";

    /// <summary>
    /// Intel Macs. Upstream's crispasr-macos.tar.gz is arm64-only - its build job runs on
    /// macos-latest, which is Apple Silicon since macos-13 was retired, so an Intel Mac gets
    /// "Bad CPU type in executable" after a 15 MB download, and Rosetta cannot bridge it
    /// (x86_64 -> arm64 only, never the reverse). Issue #13559. Until upstream ships an
    /// x86_64 or universal build, Subtitle Edit builds the x86_64 slice itself from the same
    /// pinned tag: SubtitleEdit/support-files, workflow build-crispasr-macos-x64-release.yml.
    /// It is a CPU + Accelerate build (ggml's Metal kernels crash on the AMD GPUs in Intel
    /// Macs) and targets macOS 12, and the archive's inner folder matches upstream's so the
    /// unpack path is shared.
    /// </summary>
    private const string MacIntelUrl = "https://github.com/SubtitleEdit/support-files/releases/download/crispasr-0832-macos-x64/crispasr-macos-x86_64.tar.gz";
    private const string LinuxUrl = "https://github.com/CrispStrobe/CrispASR/releases/download/v0.8.32/crispasr-linux-x86_64.tar.gz";
    private const string LinuxCudaUrl = "https://github.com/CrispStrobe/CrispASR/releases/download/v0.8.32/crispasr-linux-x86_64-cuda.tar.gz";
    private const string LinuxCuda13Url = "https://github.com/CrispStrobe/CrispASR/releases/download/v0.8.32/crispasr-linux-x86_64-cuda13.tar.gz";
    private const string LinuxVulkanUrl = "https://github.com/CrispStrobe/CrispASR/releases/download/v0.8.32/crispasr-linux-x86_64-vulkan.tar.gz";
    private const string LinuxHipUrl = "https://github.com/CrispStrobe/CrispASR/releases/download/v0.8.32/crispasr-linux-x86_64-hip.tar.gz";
    private const string LinuxArmUrl = "https://github.com/CrispStrobe/CrispASR/releases/download/v0.8.32/crispasr-linux-arm64.tar.gz";

    public CrispAsrDownloadService(HttpClient httpClient)
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
        await DownloadAndVerifyAsync(WindowsCudaUrl, DownloadHashManager.CrispAsr.WindowsCuda, stream, progress, cancellationToken);
    }

    public async Task DownloadEngineWindowsCuda13(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        await DownloadAndVerifyAsync(WindowsCuda13Url, DownloadHashManager.CrispAsr.WindowsCuda13, stream, progress, cancellationToken);
    }

    public async Task DownloadEngineWindowsVulkan(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        await DownloadAndVerifyAsync(WindowsVulkanUrl, DownloadHashManager.CrispAsr.WindowsVulkan, stream, progress, cancellationToken);
    }

    public async Task DownloadEngineWindowsCpu(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        await DownloadAndVerifyAsync(WindowsCpuUrl, DownloadHashManager.CrispAsr.WindowsCpu, stream, progress, cancellationToken);
    }

    public async Task DownloadEngineWindowsCpuLegacy(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        await DownloadAndVerifyAsync(WindowsCpuLegacyUrl, DownloadHashManager.CrispAsr.WindowsCpuLegacy, stream, progress, cancellationToken);
    }

    public async Task DownloadEngineLinuxCuda(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        await DownloadAndVerifyAsync(LinuxCudaUrl, DownloadHashManager.CrispAsr.LinuxCuda, stream, progress, cancellationToken);
    }

    public async Task DownloadEngineLinuxCuda13(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        await DownloadAndVerifyAsync(LinuxCuda13Url, DownloadHashManager.CrispAsr.LinuxCuda13, stream, progress, cancellationToken);
    }

    public async Task DownloadEngineLinuxVulkan(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        await DownloadAndVerifyAsync(LinuxVulkanUrl, DownloadHashManager.CrispAsr.LinuxVulkan, stream, progress, cancellationToken);
    }

    public async Task DownloadEngineLinuxHip(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        await DownloadAndVerifyAsync(LinuxHipUrl, DownloadHashManager.CrispAsr.LinuxHip, stream, progress, cancellationToken);
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
            throw new InvalidOperationException("No SHA-256 key is registered for the Crisp ASR runtime.");
        }

        var expected = DownloadHashManager.GetLatestKnownHash(hashKey);
        if (string.IsNullOrEmpty(expected))
        {
            throw new InvalidOperationException($"No SHA-256 is registered for Crisp ASR runtime key '{hashKey}'.");
        }

        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new InvalidOperationException("Crisp ASR runtime integrity verification requires a readable, seekable stream.");
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
                $"Crisp ASR runtime download failed integrity check (expected SHA-256 {expected}, got {actual}).");
        }
    }

    private static (string Url, string HashKey) GetDefaultDownload()
    {
        if (OperatingSystem.IsWindows())
        {
            return (WindowsVulkanUrl, DownloadHashManager.CrispAsr.WindowsVulkan);
        }

        if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? (LinuxArmUrl, DownloadHashManager.CrispAsr.LinuxArm)
                : (LinuxUrl, DownloadHashManager.CrispAsr.Linux);
        }

        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? (MacUrl, DownloadHashManager.CrispAsr.MacOs)
                : (MacIntelUrl, DownloadHashManager.CrispAsr.MacOsX64);
        }

        throw new PlatformNotSupportedException();
    }
}