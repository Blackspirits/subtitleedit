using System.Net;
using System.Text;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class SpellCheckDictionaryDownloadServiceTests
{
    private const string VoikkoReleaseUrl =
        "https://github.com/SubtitleEdit/support-files/releases/download/voikko-4.3-fi-2024-06/";

    [Theory]
    [InlineData("libvoikko-1.dll", "bfffd537ff372b425a61940d4f5ac6c80e2a745dab33cc00ac50e8f50441d1b0")]
    [InlineData("dict.zip", "98f26bb67e08288910fbf1aa92521f28bee79538ba86aefa267713333e7fa537")]
    public void VoikkoHash_MatchesPublishedReleaseDigest(string fileName, string expected)
    {
        Assert.Equal(expected, SpellCheckDictionaryDownloadService.GetExpectedVoikkoHash(VoikkoReleaseUrl + fileName));
    }

    [Fact]
    public void UnknownVoikkoAsset_FailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SpellCheckDictionaryDownloadService.GetExpectedVoikkoHash(VoikkoReleaseUrl + "unknown.zip"));
    }

    [Fact]
    public void GenericDictionaryUrl_IsNotForcedIntoVoikkoVerification()
    {
        Assert.Null(SpellCheckDictionaryDownloadService.GetExpectedVoikkoHash(
            "https://example.invalid/dictionaries/pt_PT.dic"));
    }

    [Fact]
    public async Task DownloadDictionary_TamperedVoikkoPayload_IsRejectedAndRewound()
    {
        using var httpClient = new HttpClient(new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered")));
        var service = new SpellCheckDictionaryDownloadService(httpClient);
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<IOException>(() =>
            service.DownloadDictionary(
                stream,
                VoikkoReleaseUrl + "dict.zip",
                progress: null,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task GenericDictionaryDownload_RemainsUnchanged()
    {
        var payload = Encoding.ASCII.GetBytes("dictionary-data");
        using var httpClient = new HttpClient(new StaticResponseHandler(payload));
        var service = new SpellCheckDictionaryDownloadService(httpClient);
        await using var stream = new MemoryStream();

        await service.DownloadDictionary(
            stream,
            "https://example.invalid/dictionaries/pt_PT.dic",
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(payload.Length, stream.Length);
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
