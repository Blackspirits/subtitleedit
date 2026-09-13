using System.Net;
using System.Text;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class CrispEmbedDownloadServiceTests
{
    [Theory]
    [InlineData(DownloadHashManager.CrispEmbed.WindowsCuda, "89bcdde6d81634461278bd877e6e35f477cd6771dec7de40f0cfeeca1c5b732a")]
    [InlineData(DownloadHashManager.CrispEmbed.WindowsVulkan, "ce2d3eb91b6dda3b9a50b96cf26e70010854c0b28fa38413c87cefe732dd9fc2")]
    [InlineData(DownloadHashManager.CrispEmbed.WindowsCpu, "dabf9483f1a2e6e801b85a3fe67a6dab2b55359fc604c91e80a44756a21e4b07")]
    [InlineData(DownloadHashManager.CrispEmbed.MacOs, "b85b636dfc5dfb2e9d7b7d6403931864bc42a756629a3962396cf750d6f604ca")]
    [InlineData(DownloadHashManager.CrispEmbed.Linux, "775b138650a60064b66f976da3e22c8f5b36fa605f7ebd767967fb0c3b412984")]
    [InlineData(DownloadHashManager.CrispEmbed.LinuxCuda, "3e1447bc93f8f7039c94b91d4bb97c739cc3b754d9b1ee3006616dbd445a1b6f")]
    [InlineData(DownloadHashManager.CrispEmbed.LinuxArm, "247915ad0c870814498a81731ddc037a79d00be5f05aee7c7c85a5dd5fae9c40")]
    public void RegistryHash_MatchesPublishedReleaseDigest(string key, string expected)
    {
        Assert.Equal(expected, DownloadHashManager.GetLatestKnownHash(key));
    }

    [Fact]
    public async Task DownloadEngine_TamperedPayload_RejectsDownloadedBytes()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Skip("CrispEmbed runtime is not supported on this operating system.");
        }

        using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));
        var service = new CrispEmbedDownloadService(httpClient);
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<IOException>(() =>
            service.DownloadEngine(
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
            CrispEmbedDownloadService.VerifyArchiveAsync(
                stream,
                "CrispEmbed.Unknown",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VerifyArchiveAsync_TamperedPayload_RewindsStream()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("tampered"));

        await Assert.ThrowsAsync<IOException>(() =>
            CrispEmbedDownloadService.VerifyArchiveAsync(
                stream,
                DownloadHashManager.CrispEmbed.Linux,
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
