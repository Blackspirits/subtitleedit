using Nikse.SubtitleEdit.Features.Video.TextToSpeech.VoiceManager.VoicePacks;
using Nikse.SubtitleEdit.UiLogic;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Logic.Download;

public interface IVoicePackDownloadService
{
    Task DownloadPack(VoicePack pack, Stream stream, IProgress<float>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Downloads a <see cref="VoicePack"/> zip and checks it against the catalog's SHA-256, so a
/// truncated or tampered download surfaces as an error rather than as half a voice pack.
/// </summary>
public class VoicePackDownloadService : IVoicePackDownloadService
{
    private readonly HttpClient _httpClient;

    public VoicePackDownloadService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task DownloadPack(VoicePack pack, Stream stream, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pack.Sha256))
        {
            throw new InvalidOperationException($"No SHA-256 is registered for voice pack '{pack.Name}'.");
        }

        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new InvalidOperationException("Voice pack integrity verification requires a readable, seekable stream.");
        }

        await DownloadHelper.DownloadFileAsync(_httpClient, pack.Url, stream, progress, cancellationToken);
        await VerifyPackAsync(pack, stream, cancellationToken);
    }

    internal static async Task VerifyPackAsync(VoicePack pack, Stream stream, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pack.Sha256))
        {
            throw new InvalidOperationException($"No SHA-256 is registered for voice pack '{pack.Name}'.");
        }

        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new InvalidOperationException("Voice pack integrity verification requires a readable, seekable stream.");
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

        if (!string.Equals(pack.Sha256, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Voice pack '{pack.Name}' failed integrity check (expected SHA-256 {pack.Sha256}, got {actual}).");
        }
    }
}
