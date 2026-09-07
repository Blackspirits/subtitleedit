from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    raw = p.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig")
    newline = "\r\n" if "\r\n" in text else "\n"
    old = old.replace("\n", newline)
    new = new.replace("\n", newline)
    count = text.count(old)
    assert count == 1, f"{path}: expected one anchor, found {count}"
    text = text.replace(old, new, 1)
    p.write_bytes((b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8"))


player = "src/ui/Logic/VideoPlayers/LibMpvDynamic/LibMpvDynamicPlayer.cs"

replace_once(
    player,
    """    private static string[] GetLibraryPaths()
    {
        if (OperatingSystem.IsWindows())
        {
            return
            [
                MpvPath,
                Directory.GetCurrentDirectory(),
                string.Empty,
            ];
        }
""",
    """    internal static string[] GetWindowsLibraryPaths(
        string mpvPath,
        string dataFolder,
        string baseDirectory,
        string currentDirectory)
    {
        var paths = new List<string>();

        void AddPath(string path)
        {
            if (!paths.Exists(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)))
            {
                paths.Add(path);
            }
        }

        // A configured override wins. The per-user data folder comes next so a downloaded
        // libmpv can override the installer/portable baseline without administrator rights.
        if (!string.IsNullOrWhiteSpace(mpvPath))
        {
            AddPath(mpvPath);
        }

        AddPath(dataFolder);
        AddPath(baseDirectory);
        AddPath(currentDirectory);
        AddPath(string.Empty);
        return paths.ToArray();
    }

    private static string[] GetLibraryPaths()
    {
        if (OperatingSystem.IsWindows())
        {
            return GetWindowsLibraryPaths(
                MpvPath,
                Se.DataFolder,
                AppContext.BaseDirectory,
                Directory.GetCurrentDirectory());
        }
""",
)


test_path = Path("tests/UI/Logic/VideoPlayers/LibMpvLibraryPathTests.cs")
assert not test_path.exists(), f"{test_path}: test file already exists"
test_path.write_text(
    """using Nikse.SubtitleEdit.Logic.VideoPlayers.LibMpvDynamic;

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
""",
    encoding="utf-8",
)
