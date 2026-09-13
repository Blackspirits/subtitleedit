using System.Net;
using System.Text;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class TtsDownloadServicePiperTests
{
    [Fact]
    public async Task DownloadPiper_TamperedPayload_RejectsDownloadedBytes()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));
        var service = new TtsDownloadService(httpClient);
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<IOException>(() =>
            service.DownloadPiper(
                stream,
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task VerifyPiperArchiveAsync_TamperedPayload_RejectsBytes()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("tampered"));

        await Assert.ThrowsAsync<IOException>(() =>
            TtsDownloadService.VerifyPiperArchiveAsync(
                stream,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task VerifyPiperFileAsync_TamperedPayload_DeletesFile()
    {
        var fileName = Path.Combine(Path.GetTempPath(), $"subtitleedit-piper-{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(fileName, "tampered", TestContext.Current.CancellationToken);

        try
        {
            await Assert.ThrowsAsync<IOException>(() =>
                TtsDownloadService.VerifyPiperFileAsync(
                    fileName,
                    TestContext.Current.CancellationToken));

            Assert.False(File.Exists(fileName));
        }
        finally
        {
            File.Delete(fileName);
        }
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
