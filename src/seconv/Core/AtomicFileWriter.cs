using System.Text;

namespace SeConv.Core;

/// <summary>
/// Writes output files via a temp file in the same folder that is then renamed over the target,
/// so an interrupted write (killed process, crash, power loss) leaves either the old file or the
/// new one - never a truncated one. With --overwrite the target is often the input itself, and
/// File.WriteAllText truncates before it writes (issue #15829).
/// </summary>
internal static class AtomicFileWriter
{
    public static void WriteAllText(string path, string content, Encoding encoding)
    {
        Write(path, stream =>
        {
            // StreamWriter writes the encoding's preamble at position 0, like File.WriteAllText.
            using var writer = new StreamWriter(stream, encoding, bufferSize: -1, leaveOpen: true);
            writer.Write(content);
        });
    }

    public static void Write(string path, Action<Stream> write) =>
        Write(path, write, tempPath => new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None));

    // The factory enables deterministic permission/I/O failure tests without changing
    // machine-level folder permissions. Never fall back to truncating the target.
    internal static void Write(string path, Action<Stream> write, Func<string, FileStream> createTempFile)
    {
        var target = ResolveTarget(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(target));
        if (string.IsNullOrEmpty(directory))
        {
            directory = Directory.GetCurrentDirectory();
        }

        // Same folder, so the rename stays on one volume and is atomic.
        var tempPath = Path.Combine(directory, "." + Path.GetFileName(target) + "." + Path.GetRandomFileName() + ".tmp");
        try
        {
            // If the temporary file cannot be created, fail without modifying the
            // destination. Writing directly would truncate existing subtitles before
            // the caller can detect an error (e.g. disk or permission failure).
            using (var tempStream = createTempFile(tempPath))
            {
                write(tempStream);
                tempStream.Flush(flushToDisk: true);
            }

            CopyUnixFileMode(target, tempPath);
            ReplaceFile(tempPath, target);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// A rename over a symbolic link would replace the link with a regular file - write to the
    /// file it points at instead.
    /// </summary>
    private static string ResolveTarget(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.LinkTarget != null)
            {
                return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall back to the given path.
        }

        return path;
    }

    private static void CopyUnixFileMode(string from, string to)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(from))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(to, File.GetUnixFileMode(from));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The new file keeps the default (umask) mode.
        }
    }

    private static void ReplaceFile(string tempPath, string target)
    {
        if (OperatingSystem.IsWindows() && File.Exists(target))
        {
            try
            {
                // Keeps the target's attributes, ACLs and creation time.
                File.Replace(tempPath, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // E.g. some network shares - a plain rename below.
            }
        }

        File.Move(tempPath, target, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file is harmless; the target is intact.
        }
    }
}
