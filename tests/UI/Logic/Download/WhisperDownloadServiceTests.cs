using System.Net;
using System.Text;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class WhisperDownloadServiceTests
{
    [Fact]
    public async Task DownloadWhisperCpp_TamperedPayload_RejectsDownloadedBytes()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));
        var service = new WhisperDownloadService(httpClient);
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<IOException>(() =>
            service.DownloadWhisperCpp(
                stream,
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task VerifyArchiveAsync_UnknownKey_FailsClosed()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("abc"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WhisperDownloadService.VerifyArchiveAsync(
                stream,
                "Whisper.Unknown",
                "Whisper test artifact",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VerifyFileAsync_TamperedPayload_DeletesFile()
    {
        var fileName = Path.Combine(Path.GetTempPath(), $"subtitleedit-whisper-{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(fileName, "tampered", TestContext.Current.CancellationToken);

        try
        {
            await Assert.ThrowsAsync<IOException>(() =>
                WhisperDownloadService.VerifyFileAsync(
                    fileName,
                    DownloadHashManager.WhisperConstMe.Windows,
                    "Whisper test artifact",
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
            if (request.Method == HttpMethod.Head)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
        }
    }
}
