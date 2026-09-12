using System.Net;
using System.Text;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class FfmpegLibsDownloadServiceTests
{
    [Fact]
    public void PinnedAsset_MatchesPublishedReleaseDigest()
    {
        Assert.Equal(
            "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-09-11-13-20/ffmpeg-n9.0.1-29-gad500d59cb-win64-lgpl-shared-9.0.zip",
            FfmpegLibsDownloadService.WindowsX64Url);
        Assert.Equal(
            "40eec25b2f55dcad7e4d4e640919b920d29818b56fdaf9353ce1fd8adefc9d6b",
            FfmpegLibsDownloadService.WindowsX64Sha256);
        Assert.False(FfmpegLibsDownloadService.WindowsX64Url.Contains("/latest/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DownloadAndVerifyAsync_TamperedPayload_RejectsAndDeletesFile()
    {
        var handler = new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered"));
        using var httpClient = new HttpClient(handler);
        var destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip");

        try
        {
            await Assert.ThrowsAsync<IOException>(() =>
                FfmpegLibsDownloadService.DownloadAndVerifyAsync(
                    httpClient,
                    FfmpegLibsDownloadService.WindowsX64Url,
                    FfmpegLibsDownloadService.WindowsX64Sha256,
                    destination,
                    progress: null,
                    TestContext.Current.CancellationToken));

            Assert.False(File.Exists(destination));
            Assert.Equal(1, handler.RequestCount);
        }
        finally
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
        }
    }

    [Fact]
    public async Task DownloadAndVerifyAsync_ValidPayload_PreservesFile()
    {
        var payload = Encoding.ASCII.GetBytes("abc");
        var handler = new StaticResponseHandler(payload);
        using var httpClient = new HttpClient(handler);
        var destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip");

        try
        {
            await FfmpegLibsDownloadService.DownloadAndVerifyAsync(
                httpClient,
                "https://example.invalid/ffmpeg-test.zip",
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                destination,
                progress: null,
                TestContext.Current.CancellationToken);

            Assert.True(File.Exists(destination));
            Assert.Equal(payload, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
            Assert.Equal(1, handler.RequestCount);
        }
        finally
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
        }
    }

    [Fact]
    public async Task DownloadAndVerifyAsync_MissingDigest_FailsClosedBeforeRequest()
    {
        var handler = new StaticResponseHandler(Encoding.ASCII.GetBytes("unused"));
        using var httpClient = new HttpClient(handler);
        var destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".zip");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FfmpegLibsDownloadService.DownloadAndVerifyAsync(
                httpClient,
                "https://example.invalid/ffmpeg-future.zip",
                string.Empty,
                destination,
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, handler.RequestCount);
        Assert.False(File.Exists(destination));
    }

    private sealed class StaticResponseHandler(byte[] payload) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
        }
    }
}
