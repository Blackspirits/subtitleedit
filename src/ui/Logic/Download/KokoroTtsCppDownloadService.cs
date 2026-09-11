using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nikse.SubtitleEdit.UiLogic;

namespace Nikse.SubtitleEdit.Logic.Download;

public interface IKokoroTtsCppDownloadService
{
    Task DownloadEngine(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken);
    Task DownloadModels(string modelsFolder, IProgress<float>? progress, Action<string>? titleProgress, CancellationToken cancellationToken);
}

public class KokoroTtsCppDownloadService : IKokoroTtsCppDownloadService
{
    private readonly HttpClient _httpClient;

    // kokoro.cpp release pin. Bump in lockstep with the hashes in
    // DownloadHashManager.KokoroTtsCpp (each new release: prepend the new SHA-256 at index 0).
    public const string ReleaseTag = "v0.1.2";
    private const string ReleaseUrlBase = "https://github.com/niksedk/kokoro.cpp/releases/download/" + ReleaseTag + "/";

    private const string WindowsUrl  = ReleaseUrlBase + "kokoro-tts-server-" + ReleaseTag + "-windows-x64.zip";
    private const string MacUrl      = ReleaseUrlBase + "kokoro-tts-server-" + ReleaseTag + "-macos-arm64.zip";
    private const string LinuxUrl    = ReleaseUrlBase + "kokoro-tts-server-" + ReleaseTag + "-linux-x64.zip";
    private const string LinuxArmUrl = ReleaseUrlBase + "kokoro-tts-server-" + ReleaseTag + "-linux-arm64.zip";

    private const string TtsModelFileName    = "kokoro-v1.1-zh.onnx";
    private const string VoicesModelFileName = "voices-v1.1-zh.bin";
    private const string TtsModelUrl     = "https://github.com/koth/kokoro.cpp/releases/download/voices_model_files/kokoro-v1.1-zh.onnx";
    internal const string TtsModelSha256 = "eefec708cbc7aba8e8129b5c2f7cb92e1fe7d281af1e1dd451592d9ff0714a0d";
    private const string VoicesModelUrl  = "https://github.com/koth/kokoro.cpp/releases/download/voices_model_files/voices-v1.1-zh.bin";
    internal const string VoicesModelSha256 = "e678019845e6cfe3b7c34531779396b28f509451b91e6535d5dc09bbf11a4be5";

    public KokoroTtsCppDownloadService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task DownloadEngine(Stream stream, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        await DownloadHelper.DownloadFileAsync(_httpClient, GetUrl(), stream, progress, cancellationToken);
        await VerifyArchive(stream, DownloadHashManager.ResolveKokoroTtsCppKey(), "engine", cancellationToken);
    }

    // Compares the downloaded bytes against the known SHA-256 for this key and throws on mismatch
    // so the caller's IsFaulted branch surfaces "Download failed" instead of silently unpacking a
    // truncated or tampered file. Mirrors OmniVoiceDownloadService.VerifyArchive.
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
                $"Kokoro TTS {label} download failed integrity check (expected SHA-256 {expected}, got {actual}).");
        }
    }

    public async Task DownloadModels(string modelsFolder, IProgress<float>? progress, Action<string>? titleProgress, CancellationToken cancellationToken)
    {
        var ttsPath    = Path.Combine(modelsFolder, TtsModelFileName);
        var voicesPath = Path.Combine(modelsFolder, VoicesModelFileName);
        var needTts    = !File.Exists(ttsPath);
        var needVoices = !File.Exists(voicesPath);
        var total      = (needTts ? 1 : 0) + (needVoices ? 1 : 0);
        var step       = 0;

        if (needTts)
        {
            step++;
            titleProgress?.Invoke($"Downloading Kokoro TTS models ({step}/{total}): {TtsModelFileName}");
            await DownloadAndPublishModelAsync(
                _httpClient,
                TtsModelUrl,
                ttsPath,
                TtsModelSha256,
                progress,
                cancellationToken);
        }
        if (needVoices)
        {
            step++;
            titleProgress?.Invoke($"Downloading Kokoro TTS models ({step}/{total}): {VoicesModelFileName}");
            await DownloadAndPublishModelAsync(
                _httpClient,
                VoicesModelUrl,
                voicesPath,
                VoicesModelSha256,
                progress,
                cancellationToken);
        }
    }

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
                $"No SHA-256 is registered for Kokoro TTS model '{Path.GetFileName(destinationFileName)}'.");
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
                    $"Kokoro TTS model {Path.GetFileName(destinationFileName)} failed integrity check " +
                    $"(expected SHA-256 {expectedSha256}, got {actual}).");
            }

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

    private static string GetUrl()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsUrl;
        }

        if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? LinuxArmUrl : LinuxUrl;
        }

        if (OperatingSystem.IsMacOS())
        {
            return MacUrl;
        }

        throw new PlatformNotSupportedException();
    }
}
