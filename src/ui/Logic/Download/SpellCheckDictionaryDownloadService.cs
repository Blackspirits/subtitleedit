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

    internal const string VoikkoDllUrl =
        "https://github.com/SubtitleEdit/support-files/releases/download/voikko-4.3-fi-2024-06/libvoikko-1.dll";
    internal const string VoikkoDictionaryUrl =
        "https://github.com/SubtitleEdit/support-files/releases/download/voikko-4.3-fi-2024-06/dict.zip";

    internal const string VoikkoDllSha256 =
        "bfffd537ff372b425a61940d4f5ac6c80e2a745dab33cc00ac50e8f50441d1b0";
    internal const string VoikkoDictionarySha256 =
        "98f26bb67e08288910fbf1aa92521f28bee79538ba86aefa267713333e7fa537";

    private const string VoikkoReleasePathPrefix =
        "/SubtitleEdit/support-files/releases/download/voikko-";

    private static readonly IReadOnlyDictionary<string, string> VoikkoHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [VoikkoDllUrl] = VoikkoDllSha256,
            [VoikkoDictionaryUrl] = VoikkoDictionarySha256,
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
        if (VoikkoHashes.TryGetValue(url, out var expected))
        {
            return expected;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.AbsolutePath.StartsWith(VoikkoReleasePathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"No SHA-256 is registered for Voikko URL '{url}'.");
        }

        return null;
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
