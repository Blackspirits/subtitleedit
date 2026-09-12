using System.Net;
using System.Security.Cryptography;
using System.Text;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class OmniVoiceDownloadServiceModelTests
{
    [Fact]
    public void ModelMetadata_PinsImmutableRevisionAndPublishedDigests()
    {
        Assert.Equal(
            "017094167b5c9ed565a5076ac9b3b93c5ecf5c73",
            OmniVoiceDownloadService.ModelRepoRevision);
        Assert.Equal(
            "2882d887921798aea13d45236556bdf8012842ab6f8cd2690943eead6289f298",
            OmniVoiceDownloadService.ModelBaseSha256);
        Assert.Equal(
            "83820c6316da023076af7c1d06de5e38dcd09ae9f42203675bf8b3bd9a58e330",
            OmniVoiceDownloadService.ModelTokenizerSha256);
    }

    [Fact]
    public void ModelUrls_DoNotUseMutableMain()
    {
        var baseUrl = OmniVoiceDownloadService.GetModelUrl(OmniVoiceDownloadService.ModelBaseFileName);
        var tokenizerUrl = OmniVoiceDownloadService.GetModelUrl(OmniVoiceDownloadService.ModelTokenizerFileName);

        Assert.Equal(
            "https://huggingface.co/Serveurperso/OmniVoice-GGUF/resolve/" +
            "017094167b5c9ed565a5076ac9b3b93c5ecf5c73/" +
            "omnivoice-base-Q8_0.gguf",
            baseUrl);
        Assert.Equal(
            "https://huggingface.co/Serveurperso/OmniVoice-GGUF/resolve/" +
            "017094167b5c9ed565a5076ac9b3b93c5ecf5c73/" +
            "omnivoice-tokenizer-F32.gguf",
            tokenizerUrl);
        Assert.DoesNotContain("/resolve/main/", baseUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("/resolve/main/", tokenizerUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAndPublishModelAsync_ValidPayload_PublishesVerifiedBytes()
    {
        var payload = Encoding.ASCII.GetBytes("valid-model");
        var expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, "model.gguf");

        try
        {
            using var httpClient = new HttpClient(new StaticResponseHandler(payload));

            await OmniVoiceDownloadService.DownloadAndPublishModelAsync(
                httpClient,
                "https://example.test/model.gguf",
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
    public async Task DownloadAndPublishModelAsync_TamperedPayload_PreservesExistingDestination()
    {
        var expectedPayload = Encoding.ASCII.GetBytes("expected");
        var expected = Convert.ToHexString(SHA256.HashData(expectedPayload)).ToLowerInvariant();
        var existingPayload = Encoding.ASCII.GetBytes("existing-model");
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, "model.gguf");
        await File.WriteAllBytesAsync(destination, existingPayload, TestContext.Current.CancellationToken);

        try
        {
            using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));

            await Assert.ThrowsAsync<IOException>(() =>
                OmniVoiceDownloadService.DownloadAndPublishModelAsync(
                    httpClient,
                    "https://example.test/model.gguf",
                    destination,
                    expected,
                    progress: null,
                    TestContext.Current.CancellationToken));

            Assert.Equal(
                existingPayload,
                await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
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
        var handler = new StaticResponseHandler(Encoding.ASCII.GetBytes("unused"));
        using var httpClient = new HttpClient(handler);
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, "model.gguf");

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                OmniVoiceDownloadService.DownloadAndPublishModelAsync(
                    httpClient,
                    "https://example.test/model.gguf",
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
