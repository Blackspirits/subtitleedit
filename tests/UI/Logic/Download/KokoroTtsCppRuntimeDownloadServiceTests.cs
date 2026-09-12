using System.Net;
using System.Text;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class KokoroTtsCppRuntimeDownloadServiceTests
{
    [Theory]
    [InlineData(DownloadHashManager.KokoroTtsCpp.Windows, "560014a5f82ccb2df2dc54b7701adfbad7d23d154783d70530c86a577a9ab918")]
    [InlineData(DownloadHashManager.KokoroTtsCpp.MacOs, "39a1b4e15b48b364862ba29cf923507ee759f37d420de42bd41d333cd4dcc0ab")]
    [InlineData(DownloadHashManager.KokoroTtsCpp.LinuxX64, "3383a9154a1d34f227ea4e8a1d5aff5dd60adf4061a3b14bf93e1ad8582eddf9")]
    [InlineData(DownloadHashManager.KokoroTtsCpp.LinuxArm64, "673f49ffd2c0653b195a57f566038c05b2a962defc3e1c9c2664c8306d09352d")]
    public void RegistryHash_MatchesPublishedReleaseDigest(string key, string expected)
    {
        Assert.Equal(expected, DownloadHashManager.GetLatestKnownHash(key));
    }

    [Fact]
    public async Task DownloadEngine_TamperedPayload_IsRejectedAndRewound()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Skip("Kokoro TTS runtime is not supported on this operating system.");
        }

        using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));
        var service = new KokoroTtsCppDownloadService(httpClient);
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<IOException>(() =>
            service.DownloadEngine(
                stream,
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task VerifyArchive_UnknownKey_FailsClosed()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("abc"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            KokoroTtsCppDownloadService.VerifyArchive(
                stream,
                "KokoroTtsCpp.Unknown",
                "engine",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VerifyArchive_NonSeekableStream_FailsClosed()
    {
        await using var stream = new NonSeekableReadStream(Encoding.ASCII.GetBytes("data"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            KokoroTtsCppDownloadService.VerifyArchive(
                stream,
                DownloadHashManager.KokoroTtsCpp.LinuxX64,
                "engine",
                TestContext.Current.CancellationToken));
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
