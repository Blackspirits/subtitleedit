using Nikse.SubtitleEdit.Features.Ocr;
using Nikse.SubtitleEdit.Features.Ocr.Download;
using System.Net;
using System.Text;

namespace UITests.Features.Ocr.Download;

public class PaddleOcrDownloadVerifierTests
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
    public void GetExpectedHash_MatchesPublishedReleaseDigest(string url, string expected)
    {
        Assert.Equal(expected, PaddleOcrDownloadVerifier.GetExpectedHash(url));
    }

    [Fact]
    public void EveryConfiguredArchiveUrl_HasPinnedSha256()
    {
        foreach (var downloadType in Enum.GetValues<PaddleOcrDownloadType>())
        {
            foreach (var url in PaddleOcr.GetArchive(downloadType).Urls)
            {
                Assert.False(string.IsNullOrEmpty(PaddleOcrDownloadVerifier.GetExpectedHash(url)), url);
            }
        }
    }

    [Fact]
    public void GetExpectedHash_SameFileNameFromDifferentOrigin_IsUnknown()
    {
        const string url = "https://example.invalid/PaddleOCR-CPU-v3.7.0.7z";

        Assert.Null(PaddleOcrDownloadVerifier.GetExpectedHash(url));
    }

    [Fact]
    public async Task DownloadAndVerifyAsync_UnknownAsset_FailsClosedBeforeHttp()
    {
        var handler = new StaticResponseHandler(Encoding.ASCII.GetBytes("unused"));
        using var httpClient = new HttpClient(handler);
        var destination = Path.Combine(Path.GetTempPath(), $"paddle-unknown-{Guid.NewGuid():N}.7z");

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                PaddleOcrDownloadVerifier.DownloadAndVerifyAsync(
                    httpClient,
                    "https://example.invalid/PaddleOCR-future.7z",
                    destination,
                    progress: null,
                    TestContext.Current.CancellationToken));

            Assert.Equal(0, handler.RequestCount);
            Assert.False(File.Exists(destination));
        }
        finally
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
        }
    }

    [Fact]
    public async Task DownloadAndVerifyAsync_TamperedPayload_RejectsAndDeletesFile()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));
        var destination = Path.Combine(Path.GetTempPath(), $"paddle-tampered-{Guid.NewGuid():N}.7z");
        const string url = "https://github.com/timminator/PaddleOCR-Standalone/releases/download/v3.7.0/PaddleOCR-CPU-v3.7.0.7z";

        try
        {
            await Assert.ThrowsAsync<IOException>(() =>
                PaddleOcrDownloadVerifier.DownloadAndVerifyAsync(
                    httpClient,
                    url,
                    destination,
                    progress: null,
                    TestContext.Current.CancellationToken));

            Assert.False(File.Exists(destination));
        }
        finally
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
        }
    }

    private sealed class StaticResponseHandler(byte[] payload) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (request.Method != HttpMethod.Head)
            {
                response.Content = new ByteArrayContent(payload);
            }

            return Task.FromResult(response);
        }
    }
}
