using Nikse.SubtitleEdit.Logic.Plugins;

namespace UITests.Logic;

public sealed class PluginInstallationSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PluginInstallationSafety_" + Guid.NewGuid().ToString("N"));

    public PluginInstallationSafetyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Catalog_DoesNotListUnpublishedPluginsOrBackups()
    {
        CreatePlugin("Installed", "A");
        CreatePlugin(".tmp-staging", "A");
        CreatePlugin(".bak-previous", "A");
        CreatePlugin("Data", "A");

        var installed = PluginCatalog.GetPluginsInFolder(_root);

        var plugin = Assert.Single(installed);
        Assert.Equal("Installed", Path.GetFileName(plugin.FolderPath));
    }

    [Fact]
    public void FlatZipLayout_StillPublishesAfterCatalogSkipsItsStagingDirectory()
    {
        var stage = CreatePlugin(".tmp-unique", "Demo");
        Assert.Empty(PluginCatalog.GetPluginsInFolder(_root));

        PluginDownloadService.PublishPreparedPlugin(_root, stage, "Demo", null, "Demo",
            CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_root, "Demo", PluginConstants.ManifestFileName)));
        Assert.Single(PluginCatalog.GetPluginsInFolder(_root));
    }

    [Fact]
    public void ReplacingPlugin_KeepsNewCopyAndRemovesOldBackup()
    {
        var old = CreatePlugin("Demo", "Demo");
        File.WriteAllText(Path.Combine(old, "version.txt"), "old");
        var stage = CreatePlugin(".tmp-unique", "Demo");
        File.WriteAllText(Path.Combine(stage, "version.txt"), "new");

        PluginDownloadService.PublishPreparedPlugin(_root, stage, "Demo", old, "Demo",
            CancellationToken.None);

        Assert.Equal("new", File.ReadAllText(Path.Combine(_root, "Demo", "version.txt")));
        Assert.Empty(Directory.EnumerateDirectories(_root, ".bak-*"));
    }

    [Fact]
    public void FailedFinalMove_RestoresPreviousPlugin()
    {
        var old = CreatePlugin("Demo", "Demo");
        File.WriteAllText(Path.Combine(old, "version.txt"), "old");
        var stage = CreatePlugin(".tmp-unique", "Demo");
        File.WriteAllText(Path.Combine(stage, "version.txt"), "new");
        var calls = 0;

        void FailingMove(string from, string to)
        {
            calls++;
            if (calls == 2)
            {
                throw new IOException("Simulated failure publishing the new plugin.");
            }

            Directory.Move(from, to);
        }

        Assert.Throws<IOException>(() =>
            PluginDownloadService.PublishPreparedPlugin(_root, stage, "Demo", old, "Demo",
                CancellationToken.None, FailingMove));

        Assert.Equal(2, calls);
        Assert.Equal("old", File.ReadAllText(Path.Combine(old, "version.txt")));
        Assert.True(File.Exists(Path.Combine(stage, "version.txt")));
        Assert.Empty(Directory.EnumerateDirectories(_root, ".bak-*"));
    }

    [Fact]
    public void CancelBeforeCommit_PreservesPreviousPlugin()
    {
        var old = CreatePlugin("Demo", "Demo");
        var stage = CreatePlugin(".tmp-unique", "Demo");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            PluginDownloadService.PublishPreparedPlugin(_root, stage, "Demo", old, "Demo",
                cts.Token));

        Assert.True(Directory.Exists(old));
        Assert.True(Directory.Exists(stage));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("Data")]
    [InlineData(".tmp-foo")]
    [InlineData(".bak-foo")]
    [InlineData("nested/folder")]
    [InlineData(@"nested\folder")]
    [InlineData("CON")]
    [InlineData("Demo.")]
    public void UnsafeDestination_IsRejectedWithoutDeletingFiles(string destination)
    {
        var stage = CreatePlugin(".tmp-unique", "Demo");
        var dataFolder = Path.Combine(_root, "Data");
        Directory.CreateDirectory(dataFolder);
        var marker = Path.Combine(dataFolder, "important.txt");
        File.WriteAllText(marker, "keep");

        Assert.Throws<InvalidDataException>(() =>
            PluginDownloadService.PublishPreparedPlugin(_root, stage, destination, null, "Demo",
                CancellationToken.None));

        Assert.Equal("keep", File.ReadAllText(marker));
        Assert.True(Directory.Exists(stage));
    }

    [Fact]
    public void UnrelatedExistingDirectory_IsNeverDeleted()
    {
        var stage = CreatePlugin(".tmp-unique", "Demo");
        var other = Path.Combine(_root, "Other");
        Directory.CreateDirectory(other);
        var marker = Path.Combine(other, "user-file.txt");
        File.WriteAllText(marker, "keep");

        Assert.Throws<IOException>(() =>
            PluginDownloadService.PublishPreparedPlugin(_root, stage, "Other", null, "Demo",
                CancellationToken.None));

        Assert.Equal("keep", File.ReadAllText(marker));
    }

    [Fact]
    public void MissingOrMismatchedManifest_DoesNotReplaceWorkingPlugin()
    {
        var old = CreatePlugin("Demo", "Demo");
        var stage = CreatePlugin(".tmp-unique", "Another plugin");

        Assert.Throws<InvalidDataException>(() =>
            PluginDownloadService.PublishPreparedPlugin(_root, stage, "Demo", old, "Demo",
                CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(old, PluginConstants.ManifestFileName)));
    }

    private string CreatePlugin(string folderName, string pluginName)
    {
        var folder = Path.Combine(_root, folderName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, PluginConstants.ManifestFileName),
            $$"""{"name":"{{pluginName}}","version":"1.0.0"}""");
        return folder;
    }
}
