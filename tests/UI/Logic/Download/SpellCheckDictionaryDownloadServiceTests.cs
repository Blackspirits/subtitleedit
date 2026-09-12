using System.Net;
using System.Text;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class SpellCheckDictionaryDownloadServiceTests
{
    [Theory]
    [InlineData(
        "https://github.com/SubtitleEdit/support-files/releases/download/voikko-4.3-fi-2024-06/libvoikko-1.dll",
        "bfffd537ff372b425a61940d4f5ac6c80e2a745dab33cc00ac50e8f50441d1b0")]
    [InlineData(
        "https://github.com/SubtitleEdit/support-files/releases/download/voikko-4.3-fi-2024-06/dict.zip",
        "98f26bb67e08288910fbf1aa92521f28bee79538ba86aefa267713333e7fa537")]
    public void VoikkoUrl_MatchesPublishedReleaseDigest(string url, string expected)
    {
        Assert.Equal(expected, SpellCheckDictionaryDownloadService.GetExpectedVoikkoHash(url));
    }

    [Fact]
    public void UnknownAssetOnPinnedVoikkoRelease_FailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SpellCheckDictionaryDownloadService.GetExpectedVoikkoHash(
                "https://github.com/SubtitleEdit/support-files/releases/download/voikko-4.3-fi-2024-06/future.zip"));
    }

    [Fact]
    public void PinnedVoikkoPathOnWrongOrigin_FailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SpellCheckDictionaryDownloadService.GetExpectedVoikkoHash(
                "https://example.invalid/SubtitleEdit/support-files/releases/download/voikko-4.3-fi-2024-06/dict.zip"));
    }

    [Fact]
    public void GenericDictionaryUrl_IsNotForcedIntoVoikkoVerification()
    {
        Assert.Null(SpellCheckDictionaryDownloadService.GetExpectedVoikkoHash(
            "https://example.invalid/dictionaries/dict.zip"));
    }

    [Fact]
    public async Task DownloadDictionary_TamperedVoikkoPayload_IsRejectedAndRewound()
    {
        var handler = new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered"));
        using var httpClient = new HttpClient(handler);
        var service = new SpellCheckDictionaryDownloadService(httpClient);
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<IOException>(() =>
            service.DownloadDictionary(
                stream,
                SpellCheckDictionaryDownloadService.VoikkoDictionaryUrl,
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
        Assert.Equal(new[] { HttpMethod.Head, HttpMethod.Get }, handler.RequestMethods);
    }

    [Fact]
    public async Task VerifyVoikkoDownloadAsync_ValidPayload_RewindsStream()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("abc"));
        stream.Position = stream.Length;

        await SpellCheckDictionaryDownloadService.VerifyVoikkoDownloadAsync(
            stream,
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            "test.zip",
            TestContext.Current.CancellationToken);

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task GenericDictionaryDownload_RemainsUnchanged()
    {
        var payload = Encoding.ASCII.GetBytes("dictionary-data");
        var handler = new StaticResponseHandler(payload);
        using var httpClient = new HttpClient(handler);
        var service = new SpellCheckDictionaryDownloadService(httpClient);
        await using var stream = new MemoryStream();

        await service.DownloadDictionary(
            stream,
            "https://example.invalid/dictionaries/pt_PT.dic",
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(payload.Length, stream.Length);
        Assert.Equal(new[] { HttpMethod.Head, HttpMethod.Get }, handler.RequestMethods);
    }

    private sealed class StaticResponseHandler(byte[] payload) : HttpMessageHandler
    {
        public List<HttpMethod> RequestMethods { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestMethods.Add(request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
        }
    }
}
