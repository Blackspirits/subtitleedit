using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nikse.SubtitleEdit.Logic.Download;
using Nikse.SubtitleEdit.UiLogic;

namespace Nikse.SubtitleEdit.Features.Ocr.Download;

internal static class PaddleOcrDownloadIntegrity
{
    private const string ReleaseBaseUrl = "https://github.com/timminator/PaddleOCR-Standalone/releases/download/v3.7.0/";

    private static readonly IReadOnlyDictionary<string, string> Hashes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ReleaseBaseUrl + "PaddleOCR.PP-OCRv6.support.files.VideOCR.7z"] = "7f98a187a1d8d9b5291f3be7cd6a6b693b32ddffd75d39c05d333d8f0b3ee145",
        [ReleaseBaseUrl + "PaddleOCR-CPU-v3.7.0.7z"] = "a1b597f5620d1a86cec606b50908a12fc1b215adf6807be5538b7ca6bddc9d20",
        [ReleaseBaseUrl + "PaddleOCR-GPU-v3.7.0-CUDA-11.8.7z"] = "5bfe2009cab89ce7f6b70f43f8250460ce6ccc6ccf176b95e0c363079bc4da50",
        [ReleaseBaseUrl + "PaddleOCR-GPU-v3.7.0-CUDA-12.9.7z"] = "6a2c1f17f093403c8f2f4c4c7b81148b29abe710604aca8fac403af2be173cab",
        [ReleaseBaseUrl + "PaddleOCR-CPU-v3.7.0-Linux.7z"] = "1d2bd1db1d534dcd433c2d658f1c9ed13beb92fc7201a7049d376bd15e8fc39e",
        [ReleaseBaseUrl + "PaddleOCR-GPU-v3.7.0-CUDA-11.8-Linux.7z"] = "3850afef8ba8bf9f65911e855a866f9df0de06b0b8f0030dbd827162819d7158",
        [ReleaseBaseUrl + "PaddleOCR-GPU-v3.7.0-CUDA-12.9-Linux.7z.001"] = "e154edaa5f80913d2a3aba0c05110ebf09f5d100f9db1b11e2d2d2b61bff4212",
        [ReleaseBaseUrl + "PaddleOCR-GPU-v3.7.0-CUDA-12.9-Linux.7z.002"] = "900200376f77a85fc4fc2562b1832fc547092eaa6951772894666be87585bf89",
    };

    internal static string? GetExpectedHash(string url)
    {
        return Hashes.TryGetValue(url, out var hash) ? hash : null;
    }

    internal static async Task DownloadAndVerifyAsync(
        HttpClient httpClient,
        string url,
        string destinationFileName,
        IProgress<float>? progress,
        CancellationToken cancellationToken)
    {
        var expected = GetExpectedHash(url);
        if (string.IsNullOrEmpty(expected))
        {
            throw new InvalidOperationException($"No SHA-256 is registered for Paddle OCR asset '{url}'.");
        }

        try
        {
            await DownloadHelper.DownloadFileAsync(httpClient, url, destinationFileName, progress, cancellationToken);

            var actual = await Sha256Util.ComputeSha256Async(destinationFileName, cancellationToken);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    $"Paddle OCR download failed integrity check for '{Path.GetFileName(url)}' (expected SHA-256 {expected}, got {actual ?? "<missing>"}).");
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
                // Best effort: never hide the original download/integrity failure.
            }

            throw;
        }
    }
}
