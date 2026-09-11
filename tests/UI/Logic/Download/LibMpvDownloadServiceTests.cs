using System.Net;
using System.Text;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class LibMpvDownloadServiceTests
{
    [Theory]
    [InlineData(
        "https://github.com/SubtitleEdit/support-files/releases/download/libmpv-2026-08-14b/libmpv2-win64.zip",
        "ce99ee7a9cab0ada2f696b04132def67b0978157d5f4a1a7966d04c92aebbfec")]
    [InlineData(
        "https://github.com/SubtitleEdit/support-files/releases/download/libmpv-2026-08-14b/libmpv2-win-arm64.zip",
        "d8be93f69eb102026ba81d5d237887b858701510c9e5e26996a2d30f9829df00")]
    public void GetExpectedSha256_KnownAsset_ReturnsPinnedDigest(string url, string expected)
    {
        Assert.Equal(expected, LibMpvDownloadService.GetExpectedSha256(url));
    }

    [Fact]
    public void KnownSha256_ContainsOnlyValidHexDigests()
    {
        Assert.Equal(2, LibMpvDownloadService.KnownSha256.Count);
        foreach (var hash in LibMpvDownloadService.KnownSha256.Values)
        {
            Assert.Matches("^[0-9a-f]{64}$", hash);
        }
    }

    [Fact]
    public void GetExpectedSha256_UnknownAsset_FailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            LibMpvDownloadService.GetExpectedSha256(
                "https://github.com/SubtitleEdit/support-files/releases/download/libmpv-future/libmpv2-win64-future.zip"));
    }

    [Fact]
    public async Task VerifyChecksumAsync_KnownDigest_SucceedsAndRewindsStream()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("abc"));

        await LibMpvDownloadService.VerifyChecksumAsync(
            stream,
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            TestContext.Current.CancellationToken);

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task DownloadAndVerifyAsync_TamperedPayload_RejectsDownloadedBytes()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));
        var service = new LibMpvDownloadService(httpClient);
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<IOException>(() =>
            service.DownloadAndVerifyAsync(
                stream,
                "https://example.test/libmpv.zip",
                new string('0', 64),
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
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
