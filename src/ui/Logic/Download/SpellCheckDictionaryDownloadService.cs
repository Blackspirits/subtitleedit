using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nikse.SubtitleEdit.UiLogic;

namespace Nikse.SubtitleEdit.Logic.Download;

public interface ISpellCheckDictionaryDownloadService
{
    Task DownloadDictionary(Stream stream, string url, IProgress<float>? progress, CancellationToken cancellationToken);
}

public class SpellCheckDictionaryDownloadService : ISpellCheckDictionaryDownloadService
{
    private readonly HttpClient _httpClient;

    private const string VoikkoReleaseUrlPrefix =
        "https://github.com/SubtitleEdit/support-files/releases/download/voikko-4.3-fi-2024-06/";

    private static readonly IReadOnlyDictionary<string, string> VoikkoHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["libvoikko-1.dll"] = "bfffd537ff372b425a61940d4f5ac6c80e2a745dab33cc00ac50e8f50441d1b0",
            ["dict.zip"] = "98f26bb67e08288910fbf1aa92521f28bee79538ba86aefa267713333e7fa537",
        };

    public SpellCheckDictionaryDownloadService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task DownloadDictionary(Stream stream, string url, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        var expected = GetExpectedVoikkoHash(url);
        await DownloadHelper.DownloadFileAsync(_httpClient, url, stream, progress, cancellationToken);

        if (!string.IsNullOrEmpty(expected))
        {
            await VerifyVoikkoDownloadAsync(stream, expected, Path.GetFileName(new Uri(url).AbsolutePath), cancellationToken);
        }
    }

    internal static string? GetExpectedVoikkoHash(string url)
    {
        if (!url.StartsWith(VoikkoReleaseUrlPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var fileName = Path.GetFileName(new Uri(url).AbsolutePath);
        if (string.IsNullOrEmpty(fileName) || !VoikkoHashes.TryGetValue(fileName, out var expected))
        {
            throw new InvalidOperationException($"No SHA-256 is registered for Voikko asset '{fileName}'.");
        }

        return expected;
    }

    internal static async Task VerifyVoikkoDownloadAsync(
        Stream stream,
        string expected,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new InvalidOperationException("Voikko integrity verification requires a readable, seekable stream.");
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
                $"Voikko download failed integrity check for {fileName} " +
                $"(expected SHA-256 {expected}, got {actual}).");
        }
    }
}