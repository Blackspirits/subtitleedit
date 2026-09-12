using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nikse.SubtitleEdit.UiLogic;

namespace Nikse.SubtitleEdit.Logic.Download;

public interface IOmniVoiceDownloadService
{
    Task DownloadModels(string modelsFolder, IProgress<float>? progress, Action<string>? titleProgress, CancellationToken cancellationToken);

    Task DownloadEngine(Stream stream, string windowsVariant, IProgress<float>? progress, CancellationToken cancellationToken);

    Task DownloadVoices(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken);
}

public class OmniVoiceDownloadService : IOmniVoiceDownloadService
{
    private readonly HttpClient _httpClient;

    public const string ModelBaseFileName = "omnivoice-base-Q8_0.gguf";
    public const string ModelTokenizerFileName = "omnivoice-tokenizer-F32.gguf";

    public const string WindowsVariantCpu = "cpu";
    public const string WindowsVariantVulkan = "vulkan";
    public const string WindowsVariantCuda = "cuda";

    internal const string ModelRepoRevision = "017094167b5c9ed565a5076ac9b3b93c5ecf5c73";
    internal const string ModelBaseSha256 = "2882d887921798aea13d45236556bdf8012842ab6f8cd2690943eead6289f298";
    internal const string ModelTokenizerSha256 = "83820c6316da023076af7c1d06de5e38dcd09ae9f42203675bf8b3bd9a58e330";
    private const string ModelRepoBaseUrl = "https://huggingface.co/Serveurperso/OmniVoice-GGUF/resolve/" + ModelRepoRevision + "/";

    // omnivoice.cpp release pin. Bump in lockstep with the hashes in DownloadHashManager.OmniVoice
    // (each new release: prepend the new SHA-256 at index 0, keep the previous one for "update available").
    public const string ReleaseTag = "omnivoice-2026-08-04";
    private const string ReleaseUrlBase = "https://github.com/niksedk/omnivoice.cpp/releases/download/" + ReleaseTag + "/";

    private const string WindowsCpuUrl = ReleaseUrlBase + "omnivoice-win64-cpu.zip";
    private const string WindowsVulkanUrl = ReleaseUrlBase + "omnivoice-win64-vulkan.zip";
    private const string WindowsCudaUrl = ReleaseUrlBase + "omnivoice-win64-cuda.zip";
    private const string MacOsUrl = ReleaseUrlBase + "omnivoice-macos-universal-cpu-metal.zip";
    private const string LinuxX64Url = ReleaseUrlBase + "omnivoice-linux-x64-cpu.zip";
    private const string LinuxArm64Url = ReleaseUrlBase + "omnivoice-linux-arm64-cpu.zip";

    private const string VoicesUrl = ReleaseUrlBase + "OmniVoices.zip";

    public OmniVoiceDownloadService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task DownloadModels(string modelsFolder, IProgress<float>? progress, Action<string>? titleProgress, CancellationToken cancellationToken)
    {
        var basePath = Path.Combine(modelsFolder, ModelBaseFileName);
        var tokenizerPath = Path.Combine(modelsFolder, ModelTokenizerFileName);
        var needBase = !File.Exists(basePath);
        var needTokenizer = !File.Exists(tokenizerPath);
        var total = (needBase ? 1 : 0) + (needTokenizer ? 1 : 0);
        var step = 0;

        if (needBase)
        {
            step++;
            titleProgress?.Invoke($"Downloading OmniVoice TTS models ({step}/{total}): {ModelBaseFileName}");
            await DownloadAndPublishModelAsync(
                _httpClient,
                GetModelUrl(ModelBaseFileName),
                basePath,
                ModelBaseSha256,
                progress,
                cancellationToken);
        }
        if (needTokenizer)
        {
            step++;
            titleProgress?.Invoke($"Downloading OmniVoice TTS models ({step}/{total}): {ModelTokenizerFileName}");
            await DownloadAndPublishModelAsync(
                _httpClient,
                GetModelUrl(ModelTokenizerFileName),
                tokenizerPath,
                ModelTokenizerSha256,
                progress,
                cancellationToken);
        }
    }

    internal static string GetModelUrl(string fileName) => ModelRepoBaseUrl + fileName;

    internal static async Task DownloadAndPublishModelAsync(
        HttpClient httpClient,
        string url,
        string destinationFileName,
        string expectedSha256,
        IProgress<float>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            throw new InvalidOperationException(
                $"No SHA-256 is registered for OmniVoice model '{Path.GetFileName(destinationFileName)}'.");
        }

        var tempFileName = destinationFileName + ".part";
        try
        {
            if (File.Exists(tempFileName))
            {
                File.Delete(tempFileName);
            }

            await DownloadHelper.DownloadFileAsync(
                httpClient,
                url,
                tempFileName,
                progress,
                cancellationToken);

            string actual;
            await using (var stream = File.OpenRead(tempFileName))
            {
                actual = await Sha256Util.ComputeSha256Async(stream, cancellationToken);
            }

            if (!string.Equals(expectedSha256, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    $"OmniVoice model {Path.GetFileName(destinationFileName)} failed integrity check " +
                    $"(expected SHA-256 {expectedSha256}, got {actual}).");
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempFileName, destinationFileName, true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempFileName))
                {
                    File.Delete(tempFileName);
                }
            }
            catch
            {
                // Preserve the original download/integrity error; cleanup is best-effort.
            }

            throw;
        }
    }

    public async Task DownloadEngine(Stream stream, string windowsVariant, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        await DownloadHelper.DownloadFileAsync(_httpClient, GetUrl(windowsVariant), stream, progress, cancellationToken);
        await VerifyArchive(stream, DownloadHashManager.ResolveOmniVoiceKey(windowsVariant), "engine", cancellationToken);
    }

    public async Task DownloadVoices(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        await DownloadHelper.DownloadFileAsync(_httpClient, VoicesUrl, stream, progress, cancellationToken);
        await VerifyArchive(stream, DownloadHashManager.OmniVoice.Voices, "voices", cancellationToken);
    }

    // Compares the downloaded bytes against the known SHA-256 for this key and throws on mismatch
    // so the caller's IsFaulted branch surfaces "Download failed" instead of silently unpacking a
    // truncated or tampered file. A null/unknown key (e.g. unrecognised Windows variant) skips the
    // check rather than failing closed - same policy as the rest of DownloadHashManager.
    private static async Task VerifyArchive(Stream stream, string? key, string label, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(key) || stream.Length == 0)
        {
            return;
        }

        var expected = DownloadHashManager.GetLatestKnownHash(key);
        if (string.IsNullOrEmpty(expected))
        {
            return;
        }

        stream.Position = 0;
        var actual = await Sha256Util.ComputeSha256Async(stream, cancellationToken);
        stream.Position = 0;

        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"OmniVoice {label} download failed integrity check (expected SHA-256 {expected}, got {actual}).");
        }
    }

    private static string GetUrl(string windowsVariant)
    {
        if (OperatingSystem.IsWindows())
        {
            return windowsVariant switch
            {
                WindowsVariantCuda => WindowsCudaUrl,
                WindowsVariantVulkan => WindowsVulkanUrl,
                _ => WindowsCpuUrl,
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            return MacOsUrl;
        }

        if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? LinuxArm64Url
                : LinuxX64Url;
        }

        throw new PlatformNotSupportedException();
    }
}
