using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class IndexTts25AudioCppDownloadServiceTests
{
    [Theory]
    [InlineData(DownloadHashManager.IndexTts25AudioCpp.EngineMacArm64, "9c710294a00f9f6b34377de909f511fa8648e85c00a7591062a85559613874a6")]
    [InlineData(DownloadHashManager.IndexTts25AudioCpp.EngineWindowsCpu, "6a1ca661819eaa68cc7f48af490da2afd46c3f56df8d8c59b6738dcbe3c32907")]
    [InlineData(DownloadHashManager.IndexTts25AudioCpp.EngineWindowsVulkan, "4f5e1bef273404021b5ea0f270798fe36368d536dcc433d8bec75ac803c6b7fa")]
    [InlineData(DownloadHashManager.IndexTts25AudioCpp.EngineWindowsCuda, "722545859b460e8959792798c37f0503fb6d86d616d78ba8f178249b3af2fecc")]
    [InlineData(DownloadHashManager.IndexTts25AudioCpp.EngineLinuxCpu, "13b5b0b2490a1655e90b33166baab2be4e07cc0e317b07d4b055605b5e3a5c87")]
    [InlineData(DownloadHashManager.IndexTts25AudioCpp.EngineLinuxVulkan, "133d4155df0a6867f69df221335fc84624ba40263b1d0852e4c789e986e56329")]
    [InlineData(DownloadHashManager.IndexTts25AudioCpp.EngineLinuxCuda, "68e85ab307ce54461fe6d7f088d8483d5a7418fc3ed865973292c39cb0d98706")]
    [InlineData(DownloadHashManager.IndexTts25AudioCpp.ModelQ8_0, "5e827b2072042e4a1b21ccf24a5cb4f71cb1011403067a0a9b039311d8b38628")]
    [InlineData(DownloadHashManager.IndexTts25AudioCpp.ModelF16, "87bed9b82fc8f22119a1a1042332091016c28e37f29b0e93343ccdbfa76ef66a")]
    public void RegistryHash_MatchesCurrentPublishedDigest(string key, string expected)
    {
        Assert.Equal(expected, DownloadHashManager.GetLatestKnownHash(key));
    }

    [Fact]
    public async Task DownloadEngine_TamperedPayload_IsRejectedAndRewound()
    {
        var backend = GetSupportedBackendOrSkip();
        using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));
        var service = new IndexTts25AudioCppDownloadService(httpClient);
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<IOException>(() =>
            service.DownloadEngine(
                stream,
                backend,
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task VerifyEngineArchiveAsync_NonSeekableStream_FailsClosed()
    {
        var backend = GetSupportedBackendOrSkip();
        await using var stream = new NonSeekableReadStream(Encoding.ASCII.GetBytes("data"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IndexTts25AudioCppDownloadService.VerifyEngineArchiveAsync(
                stream,
                backend,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VerifyFile_NullKey_FailsClosed()
    {
        var fileName = Path.Combine(Path.GetTempPath(), $"subtitleedit-indextts-{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(fileName, "data", TestContext.Current.CancellationToken);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                IndexTts25AudioCppDownloadService.VerifyFile(
                    fileName,
                    null,
                    "model.gguf",
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(fileName);
        }
    }

    [Fact]
    public async Task VerifyFile_UnknownKey_FailsClosed()
    {
        var fileName = Path.Combine(Path.GetTempPath(), $"subtitleedit-indextts-{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(fileName, "data", TestContext.Current.CancellationToken);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                IndexTts25AudioCppDownloadService.VerifyFile(
                    fileName,
                    "IndexTts25AudioCpp.Unknown",
                    "model.gguf",
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(fileName);
        }
    }

    [Fact]
    public async Task VerifyFile_TamperedPayload_IsRejected()
    {
        var fileName = Path.Combine(Path.GetTempPath(), $"subtitleedit-indextts-{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(fileName, "tampered", TestContext.Current.CancellationToken);

        try
        {
            await Assert.ThrowsAsync<IOException>(() =>
                IndexTts25AudioCppDownloadService.VerifyFile(
                    fileName,
                    DownloadHashManager.IndexTts25AudioCpp.ModelQ8_0,
                    "index-tts2_5-q8_0.gguf",
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(fileName);
        }
    }

    private static string GetSupportedBackendOrSkip()
    {
        if (OperatingSystem.IsMacOS())
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            {
                Assert.Skip("audio.cpp runtime archives support Apple Silicon only on macOS.");
            }

            return IndexTts25AudioCppDownloadService.BackendMetal;
        }

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                Assert.Skip("audio.cpp runtime archives support x86-64 only on Windows and Linux.");
            }

            return IndexTts25AudioCppDownloadService.BackendCpu;
        }

        Assert.Skip("audio.cpp runtime is not supported on this operating system.");
        return IndexTts25AudioCppDownloadService.BackendCpu;
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
