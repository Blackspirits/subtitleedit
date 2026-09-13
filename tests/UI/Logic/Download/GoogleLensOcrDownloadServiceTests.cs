using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class GoogleLensOcrDownloadServiceTests
{
    [Fact]
    public void ResolveWindowsDownload_PinsOfficialArchiveDigest()
    {
        var download = GoogleLensOcrDownloadService.ResolveWindowsDownload();

        Assert.Equal(
            "https://github.com/timminator/Chrome-Lens-OCR/releases/download/v3.4.0/Chrome-Lens-OCR-v3.4.0.7z",
            download.Url);
        Assert.Equal(
            "201685c3a3857515360174ab1e470c0f6d1e35fd90ece76deb74c277d17df085",
            download.Sha256);
    }

    [Fact]
    public void ResolveLinuxDownload_X64_PinsOfficialArchiveDigest()
    {
        var download = GoogleLensOcrDownloadService.ResolveLinuxDownload(Architecture.X64);

        Assert.Equal(
            "https://github.com/timminator/Chrome-Lens-OCR/releases/download/v3.4.0/Chrome-Lens-OCR-v3.4.0-Linux.7z",
            download.Url);
        Assert.Equal(
            "661348e20c12e4e43df061189bb90c46eff07ec25835532c52f6dcb1ce9d6d42",
            download.Sha256);
    }

    [Fact]
    public void ResolveLinuxDownload_Arm64_RemainsUnsupported()
    {
        Assert.Throws<PlatformNotSupportedException>(() =>
            GoogleLensOcrDownloadService.ResolveLinuxDownload(Architecture.Arm64));
    }

    [Fact]
    public async Task DownloadAndVerifyFileAsync_TamperedPayload_RejectsAndDeletesFile()
    {
        var expectedPayload = Encoding.ASCII.GetBytes("expected");
        var expected = Convert.ToHexString(SHA256.HashData(expectedPayload)).ToLowerInvariant();
        var download = new GoogleLensOcrDownloadService.DownloadInfo("https://example.test/google-lens.7z", expected);
        var fileName = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".7z");

        using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));

        await Assert.ThrowsAsync<IOException>(() =>
            GoogleLensOcrDownloadService.DownloadAndVerifyFileAsync(
                httpClient,
                download,
                fileName,
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.False(File.Exists(fileName));
    }

    [Fact]
    public async Task DownloadAndVerifyFileAsync_ValidPayload_PreservesFile()
    {
        var payload = Encoding.ASCII.GetBytes("abc");
        var expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var download = new GoogleLensOcrDownloadService.DownloadInfo("https://example.test/google-lens.7z", expected);
        var fileName = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".7z");

        try
        {
            using var httpClient = new HttpClient(new StaticResponseHandler(payload));

            await GoogleLensOcrDownloadService.DownloadAndVerifyFileAsync(
                httpClient,
                download,
                fileName,
                progress: null,
                TestContext.Current.CancellationToken);

            Assert.True(File.Exists(fileName));
            Assert.Equal(payload, await File.ReadAllBytesAsync(fileName, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(fileName);
        }
    }

    [Fact]
    public async Task DownloadAndVerifyFileAsync_MissingDigest_FailsBeforeHttp()
    {
        var handler = new StaticResponseHandler(Encoding.ASCII.GetBytes("unused"));
        using var httpClient = new HttpClient(handler);
        var download = new GoogleLensOcrDownloadService.DownloadInfo("https://example.test/google-lens.7z", string.Empty);
        var fileName = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".7z");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GoogleLensOcrDownloadService.DownloadAndVerifyFileAsync(
                httpClient,
                download,
                fileName,
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, handler.RequestCount);
        Assert.False(File.Exists(fileName));
    }

    [Fact]
    public async Task VerifyStreamAsync_TamperedPayload_RewindsStream()
    {
        var expectedPayload = Encoding.ASCII.GetBytes("expected");
        var expected = Convert.ToHexString(SHA256.HashData(expectedPayload)).ToLowerInvariant();
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("tampered"));

        await Assert.ThrowsAsync<IOException>(() =>
            GoogleLensOcrDownloadService.VerifyStreamAsync(
                stream,
                expected,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task VerifyStreamAsync_NonSeekableStream_FailsClosed()
    {
        await using var stream = new NonSeekableReadStream(Encoding.ASCII.GetBytes("data"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GoogleLensOcrDownloadService.VerifyStreamAsync(
                stream,
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                TestContext.Current.CancellationToken));
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
