using System.Net;
using System.Text;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class Qwen3TtsCppDownloadServiceTests
{
    [Theory]
    [InlineData(DownloadHashManager.Qwen3TtsCpp.WindowsVulkan, "08fad701e80340e6b073f292303a0242589260672f341c459572fe3a20742941")]
    [InlineData(DownloadHashManager.Qwen3TtsCpp.WindowsCpu, "df785c1cf96a7e960a91f98e589d602b387dbada8d441ab0825efba190498377")]
    [InlineData(DownloadHashManager.Qwen3TtsCpp.WindowsCuda, "e2897a1910781f8ad789d75c498fbcaea4fb0e619a1c92a6b1dbc648fba78c1e")]
    [InlineData(DownloadHashManager.Qwen3TtsCpp.MacOs, "ec98eaa8613310b77142530f0f7a29070b8995f28a53a9a9ca947ee69f6af374")]
    [InlineData(DownloadHashManager.Qwen3TtsCpp.LinuxX64, "ad0ad2aa49534c88b71bf8a4c9ef7ded5d663cb80658f49d4f723bae8ab8ee03")]
    [InlineData(DownloadHashManager.Qwen3TtsCpp.LinuxArm64, "60bd97d3c7008fdb093238296c1f801ee011fabccdf224f47d9e3e39ba0c2030")]
    [InlineData(DownloadHashManager.Qwen3TtsCpp.Voices, "8935dcb18c71fe261e95c0e7c8e4f6cbca153c76109bb946ba4dfe1f115fcada")]
    public void RegistryHash_MatchesPublishedReleaseDigest(string key, string expected)
    {
        Assert.Equal(expected, DownloadHashManager.GetLatestKnownHash(key));
    }

    [Fact]
    public async Task DownloadEngine_TamperedPayload_IsRejectedAndRewound()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Skip("Qwen3 TTS runtime is not supported on this operating system.");
        }

        using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));
        var service = new Qwen3TtsCppDownloadService(httpClient);
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<IOException>(() =>
            service.DownloadEngine(
                stream,
                Qwen3TtsCppDownloadService.WindowsVariantCpu,
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task DownloadVoices_TamperedPayload_IsRejectedAndRewound()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));
        var service = new Qwen3TtsCppDownloadService(httpClient);
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<IOException>(() =>
            service.DownloadVoices(
                stream,
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task VerifyArchive_NullKey_FailsClosed()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("abc"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Qwen3TtsCppDownloadService.VerifyArchive(
                stream,
                null,
                "engine",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VerifyArchive_UnknownKey_FailsClosed()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("abc"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Qwen3TtsCppDownloadService.VerifyArchive(
                stream,
                "Qwen3TtsCpp.Unknown",
                "engine",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VerifyArchive_EmptyStream_IsRejectedAndRewound()
    {
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<IOException>(() =>
            Qwen3TtsCppDownloadService.VerifyArchive(
                stream,
                DownloadHashManager.Qwen3TtsCpp.LinuxX64,
                "engine",
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task VerifyArchive_NonSeekableStream_FailsClosed()
    {
        await using var stream = new NonSeekableReadStream(Encoding.ASCII.GetBytes("data"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Qwen3TtsCppDownloadService.VerifyArchive(
                stream,
                DownloadHashManager.Qwen3TtsCpp.LinuxX64,
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
