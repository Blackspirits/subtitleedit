using System.Net;
using System.Text;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class ChatterboxTtsCppDownloadServiceTests
{
    [Fact]
    public void BaseModelUrl_IsPinnedToImmutableRevision()
    {
        var url = ChatterboxTtsCppDownloadService.GetModelUrl(
            ChatterboxTtsCppDownloadService.ModelKeyBase,
            ChatterboxTtsCppDownloadService.BaseT3FileName);

        Assert.Equal(
            "https://huggingface.co/cstr/chatterbox-GGUF/resolve/" +
            "c45504bb8d55473a2213db17ec472ed11b69056a/" +
            "chatterbox-v3-t3-q8_0.gguf",
            url);
        Assert.DoesNotContain("/resolve/main/", url, StringComparison.Ordinal);
    }

    [Fact]
    public void TurboModelUrl_IsPinnedToImmutableRevision()
    {
        var url = ChatterboxTtsCppDownloadService.GetModelUrl(
            ChatterboxTtsCppDownloadService.ModelKeyTurbo,
            ChatterboxTtsCppDownloadService.TurboT3FileName);

        Assert.Equal(
            "https://huggingface.co/cstr/chatterbox-turbo-GGUF/resolve/" +
            "b544cf8b49504d880640864a757b3ff3e4421a42/" +
            "chatterbox-turbo-t3-q8_0.gguf",
            url);
        Assert.DoesNotContain("/resolve/main/", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAndPublishModelAsync_ValidPayload_PublishesOnlyFinalFile()
    {
        var payload = Encoding.ASCII.GetBytes("valid-model");
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, "model.gguf");

        try
        {
            using var httpClient = new HttpClient(new StaticResponseHandler(payload));

            await ChatterboxTtsCppDownloadService.DownloadAndPublishModelAsync(
                httpClient,
                "https://example.test/model.gguf",
                destination,
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
    public async Task DownloadAndPublishModelAsync_FailedDownload_PreservesExistingDestination()
    {
        var existingPayload = Encoding.ASCII.GetBytes("existing-model");
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, "model.gguf");
        await File.WriteAllBytesAsync(destination, existingPayload, TestContext.Current.CancellationToken);

        try
        {
            using var httpClient = new HttpClient(new FailingGetHandler());

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ChatterboxTtsCppDownloadService.DownloadAndPublishModelAsync(
                    httpClient,
                    "https://example.test/model.gguf",
                    destination,
                    progress: null,
                    TestContext.Current.CancellationToken));

            Assert.IsType<HttpRequestException>(exception.InnerException);
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

    private sealed class FailingGetHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Head)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            throw new HttpRequestException("simulated download failure");
        }
    }
}
