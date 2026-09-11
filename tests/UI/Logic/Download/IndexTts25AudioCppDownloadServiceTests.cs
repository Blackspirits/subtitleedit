using System.Net;
using System.Text;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class IndexTts25AudioCppDownloadServiceTests
{
    [Fact]
    public async Task DownloadEngine_TamperedPayload_RejectsDownloadedBytes()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));
        var service = new IndexTts25AudioCppDownloadService(httpClient);
        var backends = service.GetAvailableBackends();
        Assert.NotEmpty(backends);
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<IOException>(() =>
            service.DownloadEngine(
                stream,
                backends[^1],
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task VerifyEngineArchiveAsync_TamperedPayload_RejectsBytes()
    {
        var service = new IndexTts25AudioCppDownloadService(new HttpClient());
        var backends = service.GetAvailableBackends();
        Assert.NotEmpty(backends);
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("tampered"));

        await Assert.ThrowsAsync<IOException>(() =>
            IndexTts25AudioCppDownloadService.VerifyEngineArchiveAsync(
                stream,
                backends[^1],
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
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
