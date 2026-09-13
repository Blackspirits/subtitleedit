using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class IndexTts25AudioCppDownloadServiceTests
{
    [Fact]
    public async Task DownloadEngine_TamperedPayload_RejectsDownloadedBytes()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));
        var service = new IndexTts25AudioCppDownloadService(httpClient);
        var backend = GetSupportedBackendOrSkip();
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
    public async Task VerifyEngineArchiveAsync_TamperedPayload_RejectsBytes()
    {
        var backend = GetSupportedBackendOrSkip();
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("tampered"));

        await Assert.ThrowsAsync<IOException>(() =>
            IndexTts25AudioCppDownloadService.VerifyEngineArchiveAsync(
                stream,
                backend,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
    }

    private static string GetSupportedBackendOrSkip()
    {
        if (OperatingSystem.IsMacOS())
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            {
                Assert.Skip("audio.cpp runtime archives support Apple Silicon only on macOS.");
                return IndexTts25AudioCppDownloadService.BackendMetal;
            }

            return IndexTts25AudioCppDownloadService.BackendMetal;
        }

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                Assert.Skip("audio.cpp runtime archives support x86-64 only on Windows and Linux.");
                return IndexTts25AudioCppDownloadService.BackendCpu;
            }

            return IndexTts25AudioCppDownloadService.BackendCpu;
        }

        Assert.Skip("audio.cpp runtime is not supported on this operating system.");
        return IndexTts25AudioCppDownloadService.BackendCpu;
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
