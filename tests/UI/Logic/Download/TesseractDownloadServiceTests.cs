using System.Net;
using System.Text;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class TesseractDownloadServiceTests
{
    [Fact]
    public void WindowsArchiveSha256_MatchesSupportFilesReleaseDigest()
    {
        Assert.Equal(
            "fef2dbb1de8f25d660301c17aff107c0d9b0dc99e0d4f0eee938eb7238d7d2dc",
            TesseractDownloadService.WindowsArchiveSha256);
    }

    [Fact]
    public async Task DownloadAndVerifyRuntimeAsync_TamperedPayload_IsRejectedAndRewound()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<IOException>(() =>
            TesseractDownloadService.DownloadAndVerifyRuntimeAsync(
                httpClient,
                "https://example.test/Tesseract553.zip",
                stream,
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task VerifyRuntimeArchiveAsync_TamperedPayload_RejectsAndRewindsStream()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("tampered"));

        await Assert.ThrowsAsync<IOException>(() =>
            TesseractDownloadService.VerifyRuntimeArchiveAsync(
                stream,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task VerifyRuntimeArchiveAsync_NonSeekableStream_FailsClosed()
    {
        await using var stream = new NonSeekableReadStream(Encoding.ASCII.GetBytes("payload"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TesseractDownloadService.VerifyRuntimeArchiveAsync(
                stream,
                TestContext.Current.CancellationToken));
    }

    private sealed class StaticResponseHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
        }
    }

    private sealed class NonSeekableReadStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;

        public override long Position
        {
            get => base.Position;
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }
}
