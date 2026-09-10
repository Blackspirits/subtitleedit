using Nikse.SubtitleEdit.Logic.VideoPlayers.LibMpvDynamic;

namespace UITests.Logic.VideoPlayers;

/// <summary>
/// The Windows installer and the in-app updater keep libmpv in different roots. Library lookup
/// must name those roots explicitly instead of depending on the process working directory.
/// </summary>
public class LibMpvLibraryPathTests
{
    [Fact]
    public void GetWindowsLibraryPaths_PutsManualOverrideFirst()
    {
        var paths = LibMpvDynamicPlayer.GetWindowsLibraryPaths("/manual", "/data", "/app", "/cwd");

        Assert.Equal("/manual", paths[0]);
    }

    [Fact]
    public void GetWindowsLibraryPaths_PutsDownloadedCopyBeforeBundledCopy()
    {
        var paths = LibMpvDynamicPlayer.GetWindowsLibraryPaths(string.Empty, "/data", "/app", "/cwd");

        Assert.Equal("/data", paths[0]);
        Assert.Equal("/app", paths[1]);
    }

    [Fact]
    public void GetWindowsLibraryPaths_DoesNotDependOnWorkingDirectoryForKnownCopies()
    {
        var paths = LibMpvDynamicPlayer.GetWindowsLibraryPaths(string.Empty, "/data", "/app", "/cwd");

        Assert.True(Array.IndexOf(paths, "/data") < Array.IndexOf(paths, "/cwd"));
        Assert.True(Array.IndexOf(paths, "/app") < Array.IndexOf(paths, "/cwd"));
        Assert.Equal(string.Empty, paths[^1]);
    }

    [Fact]
    public void GetWindowsLibraryPaths_DeduplicatesPortableRoots()
    {
        var paths = LibMpvDynamicPlayer.GetWindowsLibraryPaths(string.Empty, "/portable", "/portable", "/portable");

        Assert.Equal(["/portable", string.Empty], paths);
    }
}
