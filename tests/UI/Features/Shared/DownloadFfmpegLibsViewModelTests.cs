using Nikse.SubtitleEdit.Features.Shared;
using Nikse.SubtitleEdit.Logic.Download;

namespace UITests.Features.Shared;

public class DownloadFfmpegLibsViewModelTests
{
    [Fact]
    public void OnClosingCleanup_CancelsPendingDownload()
    {
        var service = new BlockingFfmpegLibsDownloadService();
        var vm = new DownloadFfmpegLibsViewModel(service);

        vm.StartDownload();
        Assert.True(service.Started.Wait(2000, TestContext.Current.CancellationToken));
        Assert.False(service.CapturedToken.IsCancellationRequested);

        vm.OnClosingCleanup();

        Assert.True(service.CapturedToken.IsCancellationRequested);
    }

    [Fact]
    public async Task DeleteTempFileWhenTaskCompletesAsync_WaitsThenDeletes()
    {
        var fileName = Path.Combine(Path.GetTempPath(), "se-ffmpeg-close-" + Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllText(fileName, "partial");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var cleanup = DownloadFfmpegLibsViewModel.DeleteTempFileWhenTaskCompletesAsync(completion.Task, fileName);
            Assert.False(cleanup.IsCompleted);
            Assert.True(File.Exists(fileName));

            completion.SetResult();
            await cleanup;

            Assert.False(File.Exists(fileName));
        }
        finally
        {
            File.Delete(fileName);
        }
    }

    [Fact]
    public async Task DeleteTempFileWhenTaskCompletesAsync_FaultStillDeletes()
    {
        var fileName = Path.Combine(Path.GetTempPath(), "se-ffmpeg-close-" + Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllText(fileName, "partial");

        try
        {
            await DownloadFfmpegLibsViewModel.DeleteTempFileWhenTaskCompletesAsync(
                Task.FromException(new IOException("download failed")),
                fileName);

            Assert.False(File.Exists(fileName));
        }
        finally
        {
            File.Delete(fileName);
        }
    }

    private sealed class BlockingFfmpegLibsDownloadService : IFfmpegLibsDownloadService
    {
        internal ManualResetEventSlim Started { get; } = new(false);
        internal CancellationToken CapturedToken { get; private set; }

        public async Task DownloadFfmpegLibs(string destinationFileName, IProgress<float>? progress, CancellationToken cancellationToken)
        {
            CapturedToken = cancellationToken;
            Started.Set();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
