using System.Text;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class OmniVoiceDownloadServiceTests
{
    [Theory]
    [InlineData(DownloadHashManager.OmniVoice.WindowsCpu, "8f7d78f72cfc69c904eb702497a27f9e760dcfff435d8e6acdee34cbf40a39aa")]
    [InlineData(DownloadHashManager.OmniVoice.WindowsVulkan, "a0172efa536e230c647ebfe7e0491a9f37be26622f792bcd1ff247310af9558b")]
    [InlineData(DownloadHashManager.OmniVoice.WindowsCuda, "02042cedf07e43915c24ddd14f4989648e334e0270cada8ff8074f39fa91209a")]
    [InlineData(DownloadHashManager.OmniVoice.MacOs, "d398d77684277824d5ff83e252fc9b7c518563b930a1dd717afc540f71100c00")]
    [InlineData(DownloadHashManager.OmniVoice.LinuxX64, "cc063f669a742a443866611b3f752528693e1426cf4dec0c023c1ccda86e5966")]
    [InlineData(DownloadHashManager.OmniVoice.LinuxArm64, "ca193e791973bb0016703e3b1798d1dea049cafcdb2ad8abd05ee99c14285b02")]
    [InlineData(DownloadHashManager.OmniVoice.Voices, "5d252eb78e8f4891279a36fa5127ea5ab80be35057eeaa5fadb49baeacd0c773")]
    public void RegistryHash_MatchesPublishedReleaseDigest(string key, string expected)
    {
        Assert.Equal(expected, DownloadHashManager.GetLatestKnownHash(key));
    }

    [Fact]
    public async Task VerifyArchive_UnknownKey_FailsClosed()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("abc"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OmniVoiceDownloadService.VerifyArchive(
                stream,
                "OmniVoice.Unknown",
                "engine",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task VerifyArchive_TamperedPayload_RejectsAndRewindsStream()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("tampered"));

        await Assert.ThrowsAsync<IOException>(() =>
            OmniVoiceDownloadService.VerifyArchive(
                stream,
                DownloadHashManager.OmniVoice.Voices,
                "voices",
                TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task VerifyArchive_NonSeekableStream_FailsClosed()
    {
        await using var stream = new NonSeekableReadStream(Encoding.ASCII.GetBytes("data"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OmniVoiceDownloadService.VerifyArchive(
                stream,
                DownloadHashManager.OmniVoice.Voices,
                "voices",
                TestContext.Current.CancellationToken));
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
