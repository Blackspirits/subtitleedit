using System.Net;
using System.Text;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class FfmpegDownloadServiceTests
{
    [Fact]
    public void KnownSha256_ContainsEveryPinnedFfmpegAsset()
    {
        var expected = new Dictionary<string, string>
        {
            ["ffmpeg901.zip"] = "89575634e89298191693e74d97f2a01fdb251bdfe95f4cb64f8eaa9883da9844",
            ["ffmpeg80intel.zip"] = "439c92ccbc6cf3116c4713d1724c3765f4fc68ad2351be6fa5709d7b52e1f063",
            ["ffmpeg90arm.zip"] = "21721909d4a24544359aff1ac5ce0dded8a947a2abc4c939c8d525a7c6cc881b",
        };

        Assert.Equal(expected.Count, FfmpegDownloadService.KnownSha256.Count);
        foreach (var (assetName, sha256) in expected)
        {
            Assert.True(FfmpegDownloadService.KnownSha256.TryGetValue(assetName, out var actual));
            Assert.Equal(sha256, actual);
            Assert.Matches("^[0-9a-f]{64}$", actual);
        }
    }

    [Fact]
    public void GetExpectedSha256_UnknownAsset_FailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            FfmpegDownloadService.GetExpectedSha256(
                "https://github.com/SubtitleEdit/support-files/releases/download/ffmpeg-v99/ffmpeg99.zip"));
    }

    [Fact]
    public async Task VerifyChecksumAsync_KnownDigest_SucceedsAndRewindsStream()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("abc"));

        await FfmpegDownloadService.VerifyChecksumAsync(
            stream,
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            TestContext.Current.CancellationToken);

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task VerifyChecksumAsync_Mismatch_ThrowsAndRewindsStream()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("tampered"));

        await Assert.ThrowsAsync<IOException>(() =>
            FfmpegDownloadService.VerifyChecksumAsync(
                stream,
                new string('0', 64),
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task DownloadAndVerifyAsync_TamperedPayload_RejectsDownloadedBytes()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));
        var service = new FfmpegDownloadService(httpClient);
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<IOException>(() =>
            service.DownloadAndVerifyAsync(
                stream,
                "https://example.test/ffmpeg.zip",
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
