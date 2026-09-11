using System.Net;
using System.Text;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class CrispAsrDownloadServiceTests
{
    [Theory]
    [InlineData(DownloadHashManager.CrispAsr.WindowsCuda, "9108d2be9b61415cf2c6d758d09a6fbfda369c2cda2d98f1f3d61e1326792d01")]
    [InlineData(DownloadHashManager.CrispAsr.WindowsCuda13, "e2183d839d13a2eeea175f167acfafbd66ba5cc072bd21500777c2a43e1aa8a4")]
    [InlineData(DownloadHashManager.CrispAsr.WindowsVulkan, "112a33912d464346ba1c2a75f975864a7ed0a3c1bd1ad0c3cf8806b6919efd7d")]
    [InlineData(DownloadHashManager.CrispAsr.WindowsCpu, "ac8b6caf4dd448d00c5050907275bce4d154747110c37943aa4f69ee7fac9541")]
    [InlineData(DownloadHashManager.CrispAsr.WindowsCpuLegacy, "ba4e23fb8dfcc99b8a76af034954576a75f88193e3dbf62fc774287bcbd1114b")]
    [InlineData(DownloadHashManager.CrispAsr.MacOs, "5e740d35e91a8dcaa79efd3ef0be3412de4796b68066921a9ea6984d2fc6b2ad")]
    [InlineData(DownloadHashManager.CrispAsr.MacOsX64, "a2760e096d64aeab03904f2d3a57bdee3db7f9d8ebdc621075473463236d2b01")]
    [InlineData(DownloadHashManager.CrispAsr.Linux, "6953d1e6cd8d7d828183befcf76877f1a7e3908514548a511de786887106ff08")]
    [InlineData(DownloadHashManager.CrispAsr.LinuxCuda, "becc7ae1359713af19fa09446cdc32d55c7cbca137b0a0f8cfddb6d45be04cde")]
    [InlineData(DownloadHashManager.CrispAsr.LinuxCuda13, "ef59f54e9409bb2ba52508c7a115a2f0fe73a04c0313dea79842bea3a231fb7a")]
    [InlineData(DownloadHashManager.CrispAsr.LinuxVulkan, "8d670a24830610861a3f47c4b2e78eeefc5174ef68cf415c8b0accc6545141fe")]
    [InlineData(DownloadHashManager.CrispAsr.LinuxHip, "5a3d2ff4fa02d91d56cce6d3f4162faf70a5798e3ecbb857153d56fd637de711")]
    [InlineData(DownloadHashManager.CrispAsr.LinuxArm, "eb39ca1274084add172764ce638a600e50fc4b65f4b18776aa78dcf31486570c")]
    public void RegistryHash_MatchesPublishedReleaseDigest(string key, string expected)
    {
        Assert.Equal(expected, DownloadHashManager.GetLatestKnownHash(key));
    }

    [Fact]
    public async Task DownloadEngine_TamperedPayload_RejectsDownloadedBytes()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Skip("Crisp ASR runtime is not supported on this operating system.");
        }

        using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));
        var service = new CrispAsrDownloadService(httpClient);
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
            CrispAsrDownloadService.VerifyArchiveAsync(
                stream,
                "CrispAsr.Unknown",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VerifyArchiveAsync_TamperedPayload_RewindsStream()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("tampered"));

        await Assert.ThrowsAsync<IOException>(() =>
            CrispAsrDownloadService.VerifyArchiveAsync(
                stream,
                DownloadHashManager.CrispAsr.Linux,
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
