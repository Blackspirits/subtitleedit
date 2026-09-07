using Nikse.SubtitleEdit.Logic.Plugins;
using Xunit;

namespace UITests.Logic.Plugins;

public class PluginDownloadServiceTests : IDisposable
{
    private readonly string _tempRoot;

    public PluginDownloadServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "PluginPublish_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void PublishPlugin_CancelledBeforeCommitPoint_PreservesInstalledPlugin()
    {
        var source = Path.Combine(_tempRoot, "new-plugin");
        var target = Path.Combine(_tempRoot, "installed-plugin");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "new.txt"), "new");
        File.WriteAllText(Path.Combine(target, "old.txt"), "old");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            PluginDownloadService.PublishPlugin(source, target, target, cts.Token));

        Assert.True(File.Exists(Path.Combine(target, "old.txt")));
        Assert.True(File.Exists(Path.Combine(source, "new.txt")));
    }

    [Fact]
    public void PublishPlugin_NotCancelled_ReplacesInstalledPlugin()
    {
        var source = Path.Combine(_tempRoot, "new-plugin");
        var target = Path.Combine(_tempRoot, "installed-plugin");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "new.txt"), "new");
        File.WriteAllText(Path.Combine(target, "old.txt"), "old");

        PluginDownloadService.PublishPlugin(source, target, target, CancellationToken.None);

        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(Path.Combine(target, "new.txt")));
        Assert.False(File.Exists(Path.Combine(target, "old.txt")));
    }
}
