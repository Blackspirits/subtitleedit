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
        Assert.Equal("201685c3a3857515360174ab1e470c0f6d1e35fd90ece76deb74c277d17df085", download.Sha256);
    }

    [Fact]
    public void ResolveLinuxDownload_X64_PinsOfficialArchiveDigest()
    {
        var download = GoogleLensOcrDownloadService.ResolveLinuxDownload(Architecture.X64);

        Assert.Equal(
            "https://github.com/timminator/Chrome-Lens-OCR/releases/download/v3.4.0/Chrome-Lens-OCR-v3.4.0-Linux.7z",
            download.Url);
        Assert.Equal("661348e20c12e4e43df061189bb90c46eff07ec25835532c52f6dcb1ce9d6d42", download.Sha256);
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
}
