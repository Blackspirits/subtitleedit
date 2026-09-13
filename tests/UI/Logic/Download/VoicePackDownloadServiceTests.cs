using System.Net;
using System.Text;
using Nikse.SubtitleEdit.Features.Video.TextToSpeech.VoiceManager.VoicePacks;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class VoicePackDownloadServiceTests
{
    [Theory]
    [InlineData("en-standard", "8935dcb18c71fe261e95c0e7c8e4f6cbca153c76109bb946ba4dfe1f115fcada")]
    [InlineData("de", "c541c76ab960ab31165d753385016012622bd36e43f0fdb3e53e96b31741b88d")]
    [InlineData("es", "dcd84cf4286152c2dd23436ce36d42427a587af5f9213162f4f61adc6510ec84")]
    [InlineData("fr", "f0a81f8558d9c3c42de55915b5721a98ef0d1db4f4da52d7e14d848349d7e71f")]
    [InlineData("it", "ad082e544bc515a54d52736d882b90f00e7a88f07ce5f69e66eb18ec823640f2")]
    [InlineData("nl", "260a8f4900242b9fd655d4bde7a9d5b2d9e4b5ddfd24f159e78ff1af08541d2a")]
    [InlineData("pl", "d2f43bdf5d9d955f1523d65dfe91de502892fca1708322510a9bab7d3efae55a")]
    [InlineData("pt", "7ef61545300c4cfbd0c837acb80625835f3f4e673461ac4eae3b444eb33a8685")]
    public void CatalogHash_MatchesPublishedReleaseDigest(string id, string expected)
    {
        var pack = Assert.Single(VoicePackCatalog.All, p => p.Id == id);
        Assert.Equal(expected, pack.Sha256);
    }

    [Fact]
    public async Task DownloadPack_MissingDigest_FailsBeforeHttp()
    {
        var handler = new StaticResponseHandler(Encoding.ASCII.GetBytes("unused"));
        using var httpClient = new HttpClient(handler);
        var service = new VoicePackDownloadService(httpClient);
        var pack = CreatePack(string.Empty);
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DownloadPack(pack, stream, progress: null, TestContext.Current.CancellationToken));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadPack_TamperedPayload_IsRejectedAndRewound()
    {
        var handler = new StaticResponseHandler(Encoding.ASCII.GetBytes("tampered"));
        using var httpClient = new HttpClient(handler);
        var service = new VoicePackDownloadService(httpClient);
        var pack = CreatePack("cea23dd4b87e8b8634567c2f6c9a3a31ce51ec44c2c45a70ac999aa00516a04b");
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<IOException>(() =>
            service.DownloadPack(pack, stream, progress: null, TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
        Assert.True(handler.RequestCount > 0);
    }

    [Fact]
    public async Task VerifyPackAsync_ValidPayload_RewindsStream()
    {
        var pack = CreatePack("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("abc"));
        stream.Position = stream.Length;

        await VoicePackDownloadService.VerifyPackAsync(
            pack,
            stream,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task VerifyPackAsync_EmptyStream_IsRejectedAndRewound()
    {
        var pack = VoicePackCatalog.All[0];
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<IOException>(() =>
            VoicePackDownloadService.VerifyPackAsync(
                pack,
                stream,
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task DownloadPack_NonSeekableStream_FailsBeforeHttp()
    {
        var handler = new StaticResponseHandler(Encoding.ASCII.GetBytes("unused"));
        using var httpClient = new HttpClient(handler);
        var service = new VoicePackDownloadService(httpClient);
        var pack = VoicePackCatalog.All[0];
        await using var stream = new NonSeekableReadStream(Encoding.ASCII.GetBytes("data"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DownloadPack(pack, stream, progress: null, TestContext.Current.CancellationToken));

        Assert.Equal(0, handler.RequestCount);
    }

    private static VoicePack CreatePack(string sha256)
    {
        return new VoicePack(
            "test",
            "Test pack",
            "Test",
            "xx",
            "Test voice pack",
            "https://example.test/voices.zip",
            1,
            123,
            "Test license",
            sha256);
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
