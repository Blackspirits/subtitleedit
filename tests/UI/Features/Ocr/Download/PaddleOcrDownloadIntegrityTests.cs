using System.Net;
using System.Text;
using Nikse.SubtitleEdit.Features.Ocr;
using Nikse.SubtitleEdit.Features.Ocr.Download;

namespace UITests.Features.Ocr.Download;

public class PaddleOcrDownloadIntegrityTests
{
    [Theory]
    [InlineData("https://github.com/timminator/PaddleOCR-Standalone/releases/download/v3.7.0/PaddleOCR.PP-OCRv6.support.files.VideOCR.7z", "7f98a187a1d8d9b5291f3be7cd6a6b693b32ddffd75d39c05d333d8f0b3ee145")]
    [InlineData("https://github.com/timminator/PaddleOCR-Standalone/releases/download/v3.7.0/PaddleOCR-CPU-v3.7.0.7z", "a1b597f5620d1a86cec606b50908a12fc1b215adf6807be5538b7ca6bddc9d20")]
    [InlineData("https://github.com/timminator/PaddleOCR-Standalone/releases/download/v3.7.0/PaddleOCR-GPU-v3.7.0-CUDA-11.8.7z", "5bfe2009cab89ce7f6b70f43f8250460ce6ccc6ccf176b95e0c363079bc4da50")]
    [InlineData("https://github.com/timminator/PaddleOCR-Standalone/releases/download/v3.7.0/PaddleOCR-GPU-v3.7.0-CUDA-12.9.7z", "6a2c1f17f093403c8f2f4c4c7b81148b29abe710604aca8fac403af2be173cab")]
    [InlineData("https://github.com/timminator/PaddleOCR-Standalone/releases/download/v3.7.0/PaddleOCR-CPU-v3.7.0-Linux.7z", "1d2bd1db1d534dcd433c2d658f1c9ed13beb92fc7201a7049d376bd15e8fc39e")]
    [InlineData("https://github.com/timminator/PaddleOCR-Standalone/releases/download/v3.7.0/PaddleOCR-GPU-v3.7.0-CUDA-11.8-Linux.7z", "3850afef8ba8bf9f65911e855a866f9df0de06b0b8f0030dbd827162819d7158")]
    [InlineData("https://github.com/timminator/PaddleOCR-Standalone/releases/download/v3.7.0/PaddleOCR-GPU-v3.7.0-CUDA-12.9-Linux.7z.001", "e154edaa5f80913d2a3aba0c05110ebf09f5d100f9db1b11e2d2d2b61bff4212")]
    [InlineData("https://github.com/timminator/PaddleOCR-Standalone/releases/download/v3.7.0/PaddleOCR-GPU-v3.7.0-CUDA-12.9-Linux.7z.002", "900200376f77a85fc4fc2562b1832fc547092eaa6951772894666be87585bf89")]
    public void GetExpectedSha256_MatchesPublishedReleaseDigest(string url, string expected)
    {
        Assert.Equal(expected, PaddleOcrDownloadIntegrity.GetExpectedSha256(url));
    }

    [Fact]
    public void EveryConfiguredArchiveUrl_HasPinnedSha256()
    {
        foreach (var downloadType in Enum.GetValues<PaddleOcrDownloadType>())
        {
            foreach (var url in PaddleOcr.GetArchive(downloadType).Urls)
            {
                Assert.False(string.IsNullOrEmpty(PaddleOcrDownloadIntegrity.GetExpectedSha256(url)), url);
            }
        }
    }

    [Fact]
    public async Task DownloadAndVerifyAssetAsync_TamperedPayload_RejectsAndDeletesFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"paddle-integrity-{Guid.NewGuid():N}.7z");
        try
        {
            using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));
            var url = "https://github.com/timminator/PaddleOCR-Standalone/releases/download/v3.7.0/PaddleOCR-CPU-v3.7.0.7z";

            await Assert.ThrowsAsync<IOException>(() =>
                PaddleOcrDownloadIntegrity.DownloadAndVerifyAssetAsync(
                    httpClient,
                    url,
                    tempFile,
                    progress: null,
                    TestContext.Current.CancellationToken));

            Assert.False(File.Exists(tempFile));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task DownloadAndVerifyAssetAsync_UnknownAsset_FailsClosedWithoutDownload()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"paddle-integrity-{Guid.NewGuid():N}.7z");
        using var httpClient = new HttpClient(new ThrowingHandler());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PaddleOcrDownloadIntegrity.DownloadAndVerifyAssetAsync(
                httpClient,
                "https://example.invalid/PaddleOCR-unknown.7z",
                tempFile,
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.False(File.Exists(tempFile));
    }

    private sealed class StaticResponseHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("HTTP should not be called for an unregistered Paddle OCR asset.");
        }
    }
}
