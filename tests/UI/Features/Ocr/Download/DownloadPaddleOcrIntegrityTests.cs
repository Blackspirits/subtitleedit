using System.Net;
using System.Text;
using Nikse.SubtitleEdit.Features.Ocr;
using Nikse.SubtitleEdit.Features.Ocr.Download;

namespace UITests.Features.Ocr.Download;

public class DownloadPaddleOcrIntegrityTests
{
    [Fact]
    public async Task DownloadAndVerifyAssetAsync_TamperedPayload_RejectsAndDeletesFile()
    {
        var fileName = Path.Combine(Path.GetTempPath(), $"subtitleedit-paddle-{Guid.NewGuid():N}.7z");
        var asset = new PaddleOcr.PaddleOcrAsset(
            "https://example.invalid/PaddleOCR-CPU-v3.7.0.7z",
            "a1b597f5620d1a86cec606b50908a12fc1b215adf6807be5538b7ca6bddc9d20");
        using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));

        try
        {
            await Assert.ThrowsAsync<IOException>(() =>
                DownloadPaddleOcrViewModel.DownloadAndVerifyAssetAsync(
                    httpClient,
                    asset,
                    fileName,
                    progress: null,
                    TestContext.Current.CancellationToken));

            Assert.False(File.Exists(fileName));
        }
        finally
        {
            File.Delete(fileName);
        }
    }

    [Fact]
    public async Task DownloadAndVerifyAssetAsync_MatchingPayload_KeepsFile()
    {
        var fileName = Path.Combine(Path.GetTempPath(), $"subtitleedit-paddle-{Guid.NewGuid():N}.7z");
        var payload = Encoding.ASCII.GetBytes("abc");
        var asset = new PaddleOcr.PaddleOcrAsset(
            "https://example.invalid/PaddleOCR-test.7z",
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        using var httpClient = new HttpClient(new StaticResponseHandler(payload));

        try
        {
            await DownloadPaddleOcrViewModel.DownloadAndVerifyAssetAsync(
                httpClient,
                asset,
                fileName,
                progress: null,
                TestContext.Current.CancellationToken);

            Assert.Equal(payload, await File.ReadAllBytesAsync(fileName, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(fileName);
        }
    }

    [Fact]
    public async Task DownloadAndVerifyAssetAsync_MissingDigest_FailsClosed()
    {
        var fileName = Path.Combine(Path.GetTempPath(), $"subtitleedit-paddle-{Guid.NewGuid():N}.7z");
        var asset = new PaddleOcr.PaddleOcrAsset(
            "https://example.invalid/PaddleOCR-test.7z",
            string.Empty);
        using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("unused")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DownloadPaddleOcrViewModel.DownloadAndVerifyAssetAsync(
                httpClient,
                asset,
                fileName,
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.False(File.Exists(fileName));
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
}
