using System.Net;
using System.Security.Cryptography;
using System.Text;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class KokoroTtsCppDownloadServiceTests
{
    [Fact]
    public void ModelDigests_MatchPublishedReleaseAssets()
    {
        Assert.Equal(
            "eefec708cbc7aba8e8129b5c2f7cb92e1fe7d281af1e1dd451592d9ff0714a0d",
            KokoroTtsCppDownloadService.TtsModelSha256);
        Assert.Equal(
            "e678019845e6cfe3b7c34531779396b28f509451b91e6535d5dc09bbf11a4be5",
            KokoroTtsCppDownloadService.VoicesModelSha256);
    }

    [Fact]
    public async Task DownloadAndPublishModelAsync_ValidPayload_PublishesAtomically()
    {
        var payload = Encoding.ASCII.GetBytes("valid-model");
        var expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, "model.bin");

        try
        {
            using var httpClient = new HttpClient(new StaticResponseHandler(payload));

            await KokoroTtsCppDownloadService.DownloadAndPublishModelAsync(
                httpClient,
                "https://example.test/model.bin",
                destination,
                expected,
                progress: null,
                TestContext.Current.CancellationToken);

            Assert.True(File.Exists(destination));
            Assert.False(File.Exists(destination + ".part"));
            Assert.Equal(payload, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task DownloadAndPublishModelAsync_TamperedPayload_RejectsWithoutPublishing()
    {
        var expectedPayload = Encoding.ASCII.GetBytes("expected");
        var expected = Convert.ToHexString(SHA256.HashData(expectedPayload)).ToLowerInvariant();
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, "model.bin");

        try
        {
            using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));

            await Assert.ThrowsAsync<IOException>(() =>
                KokoroTtsCppDownloadService.DownloadAndPublishModelAsync(
                    httpClient,
                    "https://example.test/model.bin",
                    destination,
                    expected,
                    progress: null,
                    TestContext.Current.CancellationToken));

            Assert.False(File.Exists(destination));
            Assert.False(File.Exists(destination + ".part"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task DownloadAndPublishModelAsync_TamperedPayload_PreservesExistingDestination()
    {
        var expectedPayload = Encoding.ASCII.GetBytes("expected");
        var expected = Convert.ToHexString(SHA256.HashData(expectedPayload)).ToLowerInvariant();
        var existingPayload = Encoding.ASCII.GetBytes("existing-model");
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, "model.bin");
        await File.WriteAllBytesAsync(destination, existingPayload, TestContext.Current.CancellationToken);

        try
        {
            using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));

            await Assert.ThrowsAsync<IOException>(() =>
                KokoroTtsCppDownloadService.DownloadAndPublishModelAsync(
                    httpClient,
                    "https://example.test/model.bin",
                    destination,
                    expected,
                    progress: null,
                    TestContext.Current.CancellationToken));

            Assert.Equal(existingPayload, await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(destination + ".part"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task DownloadAndPublishModelAsync_MissingDigest_FailsBeforeHttp()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, "model.bin");
        var handler = new StaticResponseHandler(Encoding.ASCII.GetBytes("payload"));

        try
        {
            using var httpClient = new HttpClient(handler);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                KokoroTtsCppDownloadService.DownloadAndPublishModelAsync(
                    httpClient,
                    "https://example.test/model.bin",
                    destination,
                    string.Empty,
                    progress: null,
                    TestContext.Current.CancellationToken));

            Assert.Equal(0, handler.RequestCount);
            Assert.False(File.Exists(destination));
            Assert.False(File.Exists(destination + ".part"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private sealed class StaticResponseHandler(byte[] payload) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
        }
    }
}
