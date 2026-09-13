using System.Net;
using System.Text;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class Qwen3AsrCppDownloadServiceTests
{
    [Theory]
    [InlineData(DownloadHashManager.Qwen3AsrCpp.Windows, "ad9c196aa52ceb42a3f46946644e4145fc7e297c84fa54e4cdf7fd9c22556300")]
    [InlineData(DownloadHashManager.Qwen3AsrCpp.WindowsVulkan, "a9971a24ba08765568c8a83a547924e42f15bd576b2cf594f6757fff66baf244")]
    [InlineData(DownloadHashManager.Qwen3AsrCpp.MacArm64, "a9e9f33ff8ba651591974824e886ecd28a034d00aaf8203365a7198cd5012325")]
    [InlineData(DownloadHashManager.Qwen3AsrCpp.MacX64, "6dbb978b74829c080177f6c70e89b363a8bc24d543b718ad06ecea7a7c833c53")]
    [InlineData(DownloadHashManager.Qwen3AsrCpp.Linux, "22b37a1917ca4083df7b57c62497a9f30c237ee13c2eee4b36a46320cf8506ed")]
    [InlineData(DownloadHashManager.Qwen3AsrCpp.LinuxVulkan, "d881e30dfc6f5311496355a08668d22d7f592ba4f7120a266abf8024d0f59a24")]
    [InlineData(DownloadHashManager.Qwen3AsrCpp.LinuxArm64, "8d3a1ac745fa60f5576a6db599c7b4b9bc0302ea527cbdab9be3223f80a555e4")]
    public void CurrentArchiveHash_MatchesV018ReleaseDigest(string key, string expected)
    {
        Assert.Equal(expected, DownloadHashManager.GetLatestKnownHash(key));
    }

    [Fact]
    public async Task VerifyArchiveAsync_UnknownKey_FailsClosed()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("abc"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Qwen3AsrCppDownloadService.VerifyArchiveAsync(
                stream,
                "Qwen3AsrCpp.Unknown",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DownloadEngine_TamperedPayload_RejectsDownloadedBytes()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));
        var service = new Qwen3AsrCppDownloadService(httpClient);
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<IOException>(() =>
            service.DownloadEngine(
                stream,
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
