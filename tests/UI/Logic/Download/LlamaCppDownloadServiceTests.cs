using System.Net;
using System.Text;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class LlamaCppDownloadServiceTests
{
    [Theory]
    [InlineData(DownloadHashManager.LlamaCpp.WindowsCpu, "7063dfc6b874e7eee0ddf601bdf8e70e6f4a3d708926641ffad046ec51e8e30b")]
    [InlineData(DownloadHashManager.LlamaCpp.WindowsVulkan, "a435eeaa106e4861559457fe5532aa1422f0877e7cd2bd6934c085d1d1388731")]
    [InlineData(DownloadHashManager.LlamaCpp.WindowsCuda, "8cf247aeebf5f1c9d06e9476279a9c93cfaee71d112eb7bebd68966af8a7c9bc")]
    [InlineData(DownloadHashManager.LlamaCpp.WindowsCuda13, "3e3e8c463d1d92beddca6f69d60956cdd5fad7b4d7a589ea0aecf00b51f54fbe")]
    [InlineData(DownloadHashManager.LlamaCpp.LinuxCpu, "f19a877b0d2b16cfcf19612319d06194687e9310b19b23e35abb046f55903f67")]
    [InlineData(DownloadHashManager.LlamaCpp.LinuxVulkan, "1ed6791cfe5921f8050b7af763d82aa0cb637bf97580aa4413b1b64d08eae06d")]
    [InlineData(DownloadHashManager.LlamaCpp.LinuxArm64Cpu, "301b201d85cf7e76bbf7fef5e0569bb9166325c78635ad8893c57585216c8b3b")]
    [InlineData(DownloadHashManager.LlamaCpp.LinuxArm64Vulkan, "d198a87a93142299359e4117b7447b49372cd62073e8b165cd79f6cee3f2e10f")]
    [InlineData(DownloadHashManager.LlamaCpp.MacOsArm64, "848b6cc2817aa09e615fed0813b01fc3abbc43cd4d4773cc4aff4d7ef5733784")]
    [InlineData(DownloadHashManager.LlamaCpp.MacOsX64, "980f239850ddb6d27e35bc973fcbfa1912c744a6770dbb376669ec91168e26ce")]
    [InlineData(DownloadHashManager.LlamaCpp.WindowsCudaRuntime, "8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6")]
    [InlineData(DownloadHashManager.LlamaCpp.WindowsCuda13Runtime, "1462a050eb4c684921ba51dcc4cc488a036674c3e73e9945ee705b854808d03e")]
    public void RegistryHash_MatchesPublishedReleaseDigest(string key, string expected)
    {
        Assert.Equal(expected, DownloadHashManager.GetLatestKnownHash(key));
    }

    [Fact]
    public async Task DownloadEngine_TamperedPayload_IsRejectedAndRewound()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Skip("llama.cpp runtime is not supported on this operating system.");
        }

        using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));
        var service = new LlamaCppDownloadService(httpClient);
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<IOException>(() =>
            service.DownloadEngine(
                stream,
                LlamaCppDownloadService.VariantCpu,
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task VerifyArchive_NullKey_FailsClosed()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("abc"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LlamaCppDownloadService.VerifyArchive(
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
            LlamaCppDownloadService.VerifyArchive(
                stream,
                "LlamaCpp.Unknown",
                "engine",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VerifyArchive_EmptyStream_IsRejectedAndRewound()
    {
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<IOException>(() =>
            LlamaCppDownloadService.VerifyArchive(
                stream,
                DownloadHashManager.LlamaCpp.LinuxCpu,
                "engine",
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task VerifyArchive_NonSeekableStream_FailsClosed()
    {
        await using var stream = new NonSeekableReadStream(Encoding.ASCII.GetBytes("data"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LlamaCppDownloadService.VerifyArchive(
                stream,
                DownloadHashManager.LlamaCpp.LinuxCpu,
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
