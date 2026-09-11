using System.Net;
using System.Security.Cryptography;
using System.Text;
using Nikse.SubtitleEdit.Features.Ocr;
using Nikse.SubtitleEdit.Features.Ocr.Download;

namespace UITests.Features.Ocr.Download;

public class PaddleOcrDownloadIntegrityTests
{
    [Fact]
    public async Task DownloadAndVerifyAssetAsync_ValidPayload_KeepsDownloadedFile()
    {
        var payload = Encoding.ASCII.GetBytes("abc");
        var expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var asset = new PaddleOcr.PaddleOcrAsset("https://example.test/paddle.7z", expected);
        var fileName = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".7z");

        try
        {
            using var httpClient = new HttpClient(new StaticResponseHandler(payload));

            await DownloadPaddleOcrViewModel.DownloadAndVerifyAssetAsync(
                httpClient,
                asset,
                fileName,
                progress: null,
                TestContext.Current.CancellationToken);

            Assert.True(File.Exists(fileName));
            Assert.Equal(payload, await File.ReadAllBytesAsync(fileName, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (File.Exists(fileName))
            {
                File.Delete(fileName);
            }
        }
    }

    [Fact]
    public async Task DownloadAndVerifyAssetAsync_TamperedPayload_RejectsAndDeletesFile()
    {
        var expectedPayload = Encoding.ASCII.GetBytes("expected");
        var expected = Convert.ToHexString(SHA256.HashData(expectedPayload)).ToLowerInvariant();
        var asset = new PaddleOcr.PaddleOcrAsset("https://example.test/paddle.7z", expected);
        var fileName = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".7z");

        using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));

        await Assert.ThrowsAsync<IOException>(() =>
            DownloadPaddleOcrViewModel.DownloadAndVerifyAssetAsync(
                httpClient,
                asset,
                fileName,
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.False(File.Exists(fileName));
    }

    [Fact]
    public async Task DownloadAndVerifyAssetAsync_MissingDigest_FailsClosedBeforeHttp()
    {
        var handler = new StaticResponseHandler(Encoding.ASCII.GetBytes("payload"));
        using var httpClient = new HttpClient(handler);
        var asset = new PaddleOcr.PaddleOcrAsset("https://example.test/paddle.7z", string.Empty);
        var fileName = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".7z");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DownloadPaddleOcrViewModel.DownloadAndVerifyAssetAsync(
                httpClient,
                asset,
                fileName,
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, handler.RequestCount);
        Assert.False(File.Exists(fileName));
    }

    private sealed class StaticResponseHandler(byte[] payload) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
        }
    }
}
