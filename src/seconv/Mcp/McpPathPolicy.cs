using ModelContextProtocol;

namespace SeConv.Mcp;

/// <summary>
/// Defense-in-depth path allow-list for the local MCP stdio endpoint. This is NOT
/// an OS sandbox: a privileged seconv process still has its operating-system rights.
/// </summary>
internal sealed class McpPathPolicy
{
    private readonly string[] _readRoots;
    private readonly string[] _writeRoots;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal McpPathPolicy(IEnumerable<string> readRoots, IEnumerable<string> writeRoots)
    {
        _readRoots = NormalizeRoots(readRoots);
        _writeRoots = NormalizeRoots(writeRoots);
        if (_readRoots.Length == 0)
        {
            throw new ArgumentException("MCP needs at least one --read-dir. No filesystem access is granted by default.");
        }
    }

    private static string[] NormalizeRoots(IEnumerable<string> roots)
    {
        var result = new List<string>();
        foreach (var value in roots)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Empty MCP directory scope.");
            }

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            if (!Directory.Exists(root))
            {
                throw new ArgumentException("An authorized MCP directory does not exist.");
            }

            if (!result.Any(r => r.Equals(root, PathComparison)))
            {
                result.Add(root);
            }
        }

        return result.ToArray();
    }

    internal void RequireRead(string path) => RequirePath(path, _readRoots);
    internal void RequireWrite(string path) => RequirePath(path, _writeRoots);

    internal void RequireReadPattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new McpException("A subtitle input path is required.");
        }

        // The CLI expands a wildcard only in the file name, never in a parent.
        var parent = Path.GetDirectoryName(pattern);
        var name = Path.GetFileName(pattern);
        if (string.IsNullOrWhiteSpace(name) ||
            (!string.IsNullOrEmpty(parent) && parent.IndexOfAny(['*', '?']) >= 0))
        {
            throw new McpException("Wildcards are only permitted in the input file name.");
        }

        var directory = string.IsNullOrEmpty(parent) ? Directory.GetCurrentDirectory() : parent;
        RequireRead(directory);
        if (name.IndexOfAny(['*', '?']) < 0)
        {
            RequireRead(pattern);
        }
        else if (Directory.Exists(directory))
        {
            // A safe directory containing a wildcard can still contain symlinked
            // subtitle files pointing outside the scope. Validate the resolved
            // matches, not just their parent directory.
            foreach (var match in Directory.EnumerateFiles(directory, name, SearchOption.TopDirectoryOnly))
            {
                RequireRead(match);
            }
        }
    }

    internal void RequireConversionPaths(IEnumerable<string> patterns, string? outputFolder, string? outputFilename)
    {
        var sources = patterns.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        foreach (var pattern in sources)
        {
            RequireReadPattern(pattern);
        }

        if (!string.IsNullOrWhiteSpace(outputFolder))
        {
            RequireWrite(outputFolder);
        }

        if (!string.IsNullOrWhiteSpace(outputFilename))
        {
            var output = !string.IsNullOrWhiteSpace(outputFolder) && !Path.IsPathRooted(outputFilename)
                ? Path.Combine(outputFolder, outputFilename) : outputFilename;
            RequireWrite(output);
        }
        else if (string.IsNullOrWhiteSpace(outputFolder))
        {
            // CLI default: output is written next to each input (or input glob).
            foreach (var pattern in sources)
            {
                var parent = Path.GetDirectoryName(pattern);
                RequireWrite(string.IsNullOrEmpty(parent) ? Directory.GetCurrentDirectory() : parent);
            }
        }
    }

    private static void RequirePath(string path, string[] roots)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new McpException("An authorized filesystem path is required.");
        }

        // Do not normalize away parent-directory components before checking
        // symlinks. On some systems, "allowed/link/../file" can resolve through
        // the link target before ".." is applied. The OS and Path.GetFullPath
        // need not interpret that sequence in exactly the same way.
        if (path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries).Any(component => component == ".."))
        {
            throw new McpException("Parent-directory traversal is not permitted in MCP paths.");
        }

        string absolute;
        try
        {
            absolute = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new McpException("Invalid filesystem path.");
        }

        var root = roots.Where(r => IsWithin(absolute, r)).OrderByDescending(r => r.Length).FirstOrDefault();
        if (root is null)
        {
            throw new McpException("Path is outside the MCP-approved directories.");
        }

        // Reject traversing any existing link within the approved tree. Simple
        // GetFullPath/StartsWith checks alone permit symlink escapes.
        var relative = Path.GetRelativePath(root, absolute);
        if (relative == ".")
        {
            return;
        }

        var current = root;
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new McpException("Symbolic links are not permitted inside an MCP-approved directory.");
                }
            }
            catch (FileNotFoundException)
            {
                // An output path may not exist yet; it is still lexically scoped.
            }
            catch (DirectoryNotFoundException)
            {
                // One or more later output directories may not exist yet.
            }
        }
    }

    private static bool IsWithin(string fullPath, string root) =>
        fullPath.Equals(root, PathComparison) ||
        fullPath.StartsWith(Path.EndsInDirectorySeparator(root)
            ? root : root + Path.DirectorySeparatorChar, PathComparison);
}
