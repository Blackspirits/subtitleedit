using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace Nikse.SubtitleEdit.Logic.Download;

internal static class FfmpegLibraryInstaller
{
    internal static string[] RequiredWindowsLibraryNames =>
    [
        $"avcodec-{ffmpeg.LIBAVCODEC_VERSION_MAJOR}.dll",
        $"avformat-{ffmpeg.LIBAVFORMAT_VERSION_MAJOR}.dll",
        $"avutil-{ffmpeg.LIBAVUTIL_VERSION_MAJOR}.dll",
        $"swscale-{ffmpeg.LIBSWSCALE_VERSION_MAJOR}.dll",
        $"swresample-{ffmpeg.LIBSWRESAMPLE_VERSION_MAJOR}.dll",
    ];

    internal static void Install(string zipFileName, string targetFolder, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetFolder);
        var transactionId = Guid.NewGuid().ToString("N");
        var stagingFolder = Path.Combine(targetFolder, ".ffmpeg-install-" + transactionId);
        var backupFolder = Path.Combine(targetFolder, ".ffmpeg-backup-" + transactionId);
        Directory.CreateDirectory(stagingFolder);

        try
        {
            ExtractToStaging(zipFileName, stagingFolder, cancellationToken);
            ValidateStaging(stagingFolder);
            Commit(stagingFolder, backupFolder, targetFolder, cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(stagingFolder);
        }
    }

    private static void ExtractToStaging(string zipFileName, string stagingFolder, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(zipFileName);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            if (!name.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            // Flatten exactly as the historical installer did, but do it only in staging.
            // Duplicate leaf names are rejected rather than silently selecting one archive entry.
            entry.ExtractToFile(Path.Combine(stagingFolder, entry.Name), overwrite: false);
        }
    }

    private static void ValidateStaging(string stagingFolder)
    {
        var missing = RequiredWindowsLibraryNames
            .Where(name => !File.Exists(Path.Combine(stagingFolder, name)))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "Downloaded FFmpeg archive is incomplete. Missing required libraries: " + string.Join(", ", missing));
        }
    }

    private static void Commit(string stagingFolder, string backupFolder, string targetFolder, CancellationToken cancellationToken)
    {
        var stagedFiles = Directory.GetFiles(stagingFolder)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var installed = new List<string>();
        var backedUp = new List<string>();
        Directory.CreateDirectory(backupFolder);

        try
        {
            foreach (var stagedPath in stagedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(stagedPath);
                var targetPath = Path.Combine(targetFolder, fileName);
                var backupPath = Path.Combine(backupFolder, fileName);

                if (Directory.Exists(targetPath))
                {
                    throw new IOException($"Cannot install FFmpeg library '{fileName}' because a directory already exists at that path.");
                }

                if (File.Exists(targetPath))
                {
                    File.Move(targetPath, backupPath);
                    backedUp.Add(fileName);
                }

                File.Move(stagedPath, targetPath);
                installed.Add(fileName);
            }
        }
        catch (Exception installException)
        {
            try
            {
                RollBack(targetFolder, backupFolder, installed, backedUp);
            }
            catch (Exception rollbackException)
            {
                // Never hide that the active folder may now need manual repair. Keep the backup
                // directory intact so the original bytes remain recoverable.
                throw new AggregateException(
                    "FFmpeg library installation failed and rollback could not restore the previous installation.",
                    installException,
                    rollbackException);
            }

            TryDeleteDirectory(backupFolder);
            throw;
        }

        TryDeleteDirectory(backupFolder);
    }

    private static void RollBack(string targetFolder, string backupFolder, List<string> installed, List<string> backedUp)
    {
        for (var i = installed.Count - 1; i >= 0; i--)
        {
            var targetPath = Path.Combine(targetFolder, installed[i]);
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }
        }

        for (var i = backedUp.Count - 1; i >= 0; i--)
        {
            var fileName = backedUp[i];
            var backupPath = Path.Combine(backupFolder, fileName);
            var targetPath = Path.Combine(targetFolder, fileName);
            if (!File.Exists(backupPath))
            {
                continue;
            }

            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }

            File.Move(backupPath, targetPath);
        }
    }

    private static void TryDeleteDirectory(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup. Installation/rollback success is determined before this point.
        }
    }
}
