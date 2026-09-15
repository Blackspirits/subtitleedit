using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Logic.Download;

public class FfmpegLibsDownloadServiceTests
{
    [Fact]
    public void WindowsArchive_IsPinnedToReviewedAutobuildAndDigest()
    {
        Assert.DoesNotContain("/releases/download/latest/", FfmpegLibsDownloadService.WindowsX64Url, StringComparison.Ordinal);
        Assert.Contains(FfmpegLibsDownloadService.WindowsX64ReleaseTag, FfmpegLibsDownloadService.WindowsX64Url, StringComparison.Ordinal);
        Assert.EndsWith("/" + FfmpegLibsDownloadService.WindowsX64AssetName, FfmpegLibsDownloadService.WindowsX64Url, StringComparison.Ordinal);
        Assert.Equal(64, FfmpegLibsDownloadService.WindowsX64Sha256.Length);
        Assert.Matches("^[0-9a-f]{64}$", FfmpegLibsDownloadService.WindowsX64Sha256);
    }

    [Fact]
    public async Task VerifySha256Async_MatchingDigest_KeepsFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "FfmpegLibsHashMatch_" + Guid.NewGuid().ToString("N") + ".zip");
        await File.WriteAllTextAsync(path, "abc", TestContext.Current.CancellationToken);
        try
        {
            await FfmpegLibsDownloadService.VerifySha256Async(
                path,
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                TestContext.Current.CancellationToken);

            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task VerifySha256Async_Mismatch_ThrowsAndDeletesFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "FfmpegLibsHashMismatch_" + Guid.NewGuid().ToString("N") + ".zip");
        await File.WriteAllTextAsync(path, "not the reviewed FFmpeg archive", TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                FfmpegLibsDownloadService.VerifySha256Async(
                    path,
                    FfmpegLibsDownloadService.WindowsX64Sha256,
                    TestContext.Current.CancellationToken));

            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
