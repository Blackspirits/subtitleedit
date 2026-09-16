using Nikse.SubtitleEdit.Logic.Download;
using System.IO.Compression;

namespace UITests.Logic.Download;

public class FfmpegLibraryInstallerTests
{
    [Fact]
    public void Install_CompleteArchive_CommitsRequiredLibrariesAndPreservesUnrelatedFiles()
    {
        var root = MakeRoot();
        var target = Path.Combine(root, "libs");
        var zip = Path.Combine(root, "ffmpeg.zip");
        var required = FfmpegLibraryInstaller.RequiredWindowsLibraryNames;
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, required[0]), "old-first");
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep-me");
        CreateArchive(zip, includeAllRequired: true, includeExtraDll: true);

        try
        {
            FfmpegLibraryInstaller.Install(zip, target, CancellationToken.None);

            foreach (var name in required)
            {
                Assert.Equal("new:" + name, File.ReadAllText(Path.Combine(target, name)));
            }

            Assert.True(File.Exists(Path.Combine(target, "avdevice-extra.dll")));
            Assert.Equal("keep-me", File.ReadAllText(Path.Combine(target, "keep.txt")));
            Assert.Empty(Directory.GetDirectories(target, ".ffmpeg-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Install_IncompleteArchive_LeavesExistingInstallationUntouched()
    {
        var root = MakeRoot();
        var target = Path.Combine(root, "libs");
        var zip = Path.Combine(root, "ffmpeg.zip");
        var required = FfmpegLibraryInstaller.RequiredWindowsLibraryNames;
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, required[0]), "old-first");
        CreateArchive(zip, includeAllRequired: false, includeExtraDll: false);

        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                FfmpegLibraryInstaller.Install(zip, target, CancellationToken.None));

            Assert.Contains("Missing required libraries", exception.Message);
            Assert.Equal("old-first", File.ReadAllText(Path.Combine(target, required[0])));
            Assert.Empty(Directory.GetDirectories(target, ".ffmpeg-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Install_CommitFailure_RollsBackAlreadyReplacedLibrary()
    {
        var root = MakeRoot();
        var target = Path.Combine(root, "libs");
        var zip = Path.Combine(root, "ffmpeg.zip");
        var required = FfmpegLibraryInstaller.RequiredWindowsLibraryNames
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, required[0]), "old-first");

        // Commit order is lexical. The first library is replaced; a directory at the second
        // target then forces failure so rollback has to restore bytes already changed.
        Directory.CreateDirectory(Path.Combine(target, required[1]));
        CreateArchive(zip, includeAllRequired: true, includeExtraDll: false);

        try
        {
            Assert.Throws<IOException>(() =>
                FfmpegLibraryInstaller.Install(zip, target, CancellationToken.None));

            Assert.Equal("old-first", File.ReadAllText(Path.Combine(target, required[0])));
            Assert.True(Directory.Exists(Path.Combine(target, required[1])));
            Assert.Empty(Directory.GetDirectories(target, ".ffmpeg-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Install_CanceledBeforeCommit_LeavesExistingInstallationUntouched()
    {
        var root = MakeRoot();
        var target = Path.Combine(root, "libs");
        var zip = Path.Combine(root, "ffmpeg.zip");
        var required = FfmpegLibraryInstaller.RequiredWindowsLibraryNames;
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, required[0]), "old-first");
        CreateArchive(zip, includeAllRequired: true, includeExtraDll: false);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            Assert.Throws<OperationCanceledException>(() =>
                FfmpegLibraryInstaller.Install(zip, target, cts.Token));

            Assert.Equal("old-first", File.ReadAllText(Path.Combine(target, required[0])));
            Assert.Empty(Directory.GetDirectories(target, ".ffmpeg-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string MakeRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "se-ffmpeg-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CreateArchive(string path, bool includeAllRequired, bool includeExtraDll)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var required = FfmpegLibraryInstaller.RequiredWindowsLibraryNames;
        var count = includeAllRequired ? required.Length : 1;
        for (var i = 0; i < count; i++)
        {
            AddEntry(archive, "ffmpeg/bin/" + required[i], "new:" + required[i]);
        }

        if (includeExtraDll)
        {
            AddEntry(archive, "ffmpeg/bin/avdevice-extra.dll", "extra");
        }

        AddEntry(archive, "ffmpeg/bin/ffmpeg.exe", "ignored");
        AddEntry(archive, "ffmpeg/include/avcodec.h", "ignored");
    }

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }
}
