using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class LibVlcDownloadServiceTests
{
    [Fact]
    public void ResolveWindowsDownload_X64_PinsOfficialArchiveDigest()
    {
        var download = LibVlcDownloadService.ResolveWindowsDownload(Architecture.X64);

        Assert.Equal("https://get.videolan.org/vlc/3.0.23/win64/vlc-3.0.23-win64.7z", download.Url);
        Assert.Equal("eb4fd8a28291da73608c733786a09610fea865fbe94113bcb60b91c1ebb8404a", download.Sha256);
    }

    [Fact]
    public void ResolveWindowsDownload_X86_PinsOfficialArchiveDigest()
    {
        var download = LibVlcDownloadService.ResolveWindowsDownload(Architecture.X86);

        Assert.Equal("https://get.videolan.org/vlc/3.0.23/win32/vlc-3.0.23-win32.7z", download.Url);
        Assert.Equal("f148ff49cdac6c0b6b7018ad7c4e6cd24c99bc6c2dea8258d82684261a639017", download.Sha256);
    }

    [Fact]
    public void ResolveMacDownload_X64_PinsSupportFilesDigest()
    {
        var download = LibVlcDownloadService.ResolveMacDownload(Architecture.X64);

        Assert.Equal("https://github.com/SubtitleEdit/support-files/releases/download/vlc3/libvlc-osx64.7z", download.Url);
        Assert.Equal("301c3c4a78ae2339d075f557af7ab0006c427dbd3e903c4778c59de9684c353a", download.Sha256);
    }

    [Fact]
    public async Task DownloadAndVerifyFileAsync_TamperedPayload_RejectsAndDeletesFile()
    {
        var expectedPayload = Encoding.ASCII.GetBytes("expected");
        var expected = Convert.ToHexString(SHA256.HashData(expectedPayload)).ToLowerInvariant();
        var download = new LibVlcDownloadService.DownloadInfo("https://example.test/libvlc.7z", expected);
        var fileName = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".7z");

        using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));

        await Assert.ThrowsAsync<IOException>(() =>
            LibVlcDownloadService.DownloadAndVerifyFileAsync(
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
        var download = new LibVlcDownloadService.DownloadInfo("https://example.test/libvlc.7z", expected);
        var fileName = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".7z");

        try
        {
            using var httpClient = new HttpClient(new StaticResponseHandler(payload));

            await LibVlcDownloadService.DownloadAndVerifyFileAsync(
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
        var download = new LibVlcDownloadService.DownloadInfo("https://example.test/libvlc.7z", string.Empty);
        var fileName = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".7z");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LibVlcDownloadService.DownloadAndVerifyFileAsync(
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
            LibVlcDownloadService.VerifyStreamAsync(
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
            LibVlcDownloadService.VerifyStreamAsync(
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
