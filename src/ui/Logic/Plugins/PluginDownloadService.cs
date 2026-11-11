using Nikse.SubtitleEdit.Logic.Compression;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.Logic.Download;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Logic.Plugins;

public class PluginDownloadService : IPluginDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly IZipUnpacker _zipUnpacker;
    private readonly IPluginCatalog _pluginCatalog;

    public PluginDownloadService(HttpClient httpClient, IZipUnpacker zipUnpacker, IPluginCatalog pluginCatalog)
    {
        _httpClient = httpClient;
        _zipUnpacker = zipUnpacker;
        _pluginCatalog = pluginCatalog;
    }

    public async Task<PluginIndex> GetIndexAsync(CancellationToken cancellationToken)
    {
        await using var stream = await _httpClient.GetStreamAsync(PluginConstants.OnlineIndexUrl, cancellationToken);
        var index = await JsonSerializer.DeserializeAsync(stream, PluginJsonContext.Default.PluginIndex, cancellationToken);
        return index ?? new PluginIndex();
    }

    public async Task InstallAsync(PluginIndexEntry entry, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        var downloadUrl = PluginPlatform.ResolveDownloadUrl(entry);
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            throw new InvalidOperationException($"Plugin '{entry.Name}' has no download for this platform ({PluginPlatform.GetCurrentKey() ?? "unknown"}).");
        }

        Directory.CreateDirectory(Se.PluginsFolder);

        // Unpack into a temp folder inside the plugins folder so the final move stays on the same volume.
        var tempDirectory = Path.Combine(Se.PluginsFolder, ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDirectory);

            using var zipStream = new MemoryStream();
            await DownloadHelper.DownloadFileAsync(_httpClient, downloadUrl, zipStream, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // Unzip and the subsequent file moves are synchronous; running them on a worker
            // thread keeps the UI responsive (notably for the cancel-download button) and lets
            // ThrowIfCancellationRequested between phases stop work as soon as the user reacts.
            await Task.Run(() =>
            {
                zipStream.Position = 0;
                _zipUnpacker.UnpackZipStream(zipStream, tempDirectory);
                cancellationToken.ThrowIfCancellationRequested();

                var rootEntries = Directory.GetFileSystemEntries(tempDirectory);
                string source;
                string targetName;
                if (rootEntries.Length == 1 && Directory.Exists(rootEntries[0]))
                {
                    source = rootEntries[0];
                    targetName = Path.GetFileName(rootEntries[0]);
                }
                else
                {
                    source = tempDirectory;
                    targetName = SanitizeFolderName(entry.Name);
                }

                // The catalog deliberately ignores .tmp- staging folders.
                // Validate the incoming plugin before moving the installed version.
                var existing = _pluginCatalog.GetPlugins()
                    .FirstOrDefault(p => p.Manifest.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
                PublishPreparedPlugin(Se.PluginsFolder, source, targetName, existing?.FolderPath,
                    entry.Name, cancellationToken);
            }, cancellationToken);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
            catch
            {
                // ignore - leftover temp folder, harmless
            }
        }
    }

    /// <summary>
    /// Publish an unpacked plugin without losing the previous installation if the final move
    /// fails. All three directories are siblings on one volume; the backup is retained if
    /// restoring it itself fails. The optional move operation enables deterministic failure
    /// injection in tests.
    /// </summary>
    internal static void PublishPreparedPlugin(
        string pluginsRoot, string sourceDirectory, string targetName,
        string? previousDirectory, string expectedPluginName, CancellationToken cancellationToken,
        Action<string, string>? moveDirectory = null)
    {
        var root = Path.GetFullPath(pluginsRoot);
        ValidateFolderName(targetName);
        var target = Path.Combine(root, targetName);
        var source = Path.GetFullPath(sourceDirectory);
        var existing = previousDirectory == null ? null : Path.GetFullPath(previousDirectory);

        // The source must be inside our own .tmp- staging directory, never in an
        // arbitrary folder supplied by an archive or plugin index.
        var stage = source;
        while (!SamePath(Path.GetDirectoryName(stage), root))
        {
            var parent = Path.GetDirectoryName(stage);
            if (parent == null || SamePath(parent, stage))
            {
                throw new InvalidDataException("Plugin staging directory is outside the plugins folder.");
            }

            stage = parent;
        }

        if (!Path.GetFileName(stage).StartsWith(".tmp-", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Plugin source is not inside a managed staging directory.");
        }

        var manifestPath = Path.Combine(source, PluginConstants.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException("The downloaded plugin has no plugin.json manifest.");
        }

        using (var manifestStream = File.OpenRead(manifestPath))
        {
            var manifest = JsonSerializer.Deserialize(manifestStream, PluginJsonContext.Default.PluginManifest);
            if (manifest == null ||
                !string.Equals(manifest.Name, expectedPluginName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Downloaded plugin name does not match the catalog entry.");
            }
        }

        if (existing != null)
        {
            if (!SamePath(Path.GetDirectoryName(existing), root) ||
                IsReservedFolderName(Path.GetFileName(existing)) ||
                new DirectoryInfo(existing).LinkTarget != null)
            {
                throw new InvalidDataException("Installed plugin path is not a safe direct child.");
            }

            if (!Directory.Exists(existing))
            {
                throw new IOException("The previous plugin directory disappeared during the update.");
            }
        }

        // An unrelated folder (including Plugins/Data) must never be removed.
        if (Directory.Exists(target) && (existing == null || !SamePath(existing, target)))
        {
            throw new IOException("The target plugin directory is occupied by another folder.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        moveDirectory ??= Directory.Move;
        var backup = Path.Combine(root, ".bak-" + Guid.NewGuid().ToString("N"));
        var movedPrevious = false;
        var published = false;
        try
        {
            if (existing != null)
            {
                moveDirectory(existing, backup);
                movedPrevious = true;
            }

            // No cancellation point after the previous directory is moved:
            // either publish the new plugin or restore the old one.
            moveDirectory(source, target);
            published = true;
        }
        catch (Exception publishError)
        {
            if (movedPrevious && Directory.Exists(backup) && existing != null)
            {
                try
                {
                    Directory.Move(backup, existing);
                }
                catch (Exception restoreError)
                {
                    throw new IOException(
                        "Could not restore the previous plugin. Its backup remains at " + backup,
                        new AggregateException(publishError, restoreError));
                }
            }

            throw;
        }
        finally
        {
            if (published && movedPrevious && Directory.Exists(backup))
            {
                try
                {
                    Directory.Delete(backup, recursive: true);
                }
                catch (Exception cleanupError)
                {
                    // The new plugin has been published; retaining a hidden backup is
                    // safer than treating a successful replacement as a failed install.
                    Se.LogError(cleanupError, "Could not remove plugin backup: " + backup);
                }
            }
        }
    }

    private static bool SamePath(string? left, string? right)
    {
        if (left == null || right == null)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), comparison);
    }

    private static bool IsReservedFolderName(string name)
    {
        return name.Equals("Data", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith(".tmp-", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith(".bak-", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            IsReservedFolderName(name) ||
            name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0 ||
            name.EndsWith('.') || name.EndsWith(' ') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException("Unsafe plugin folder name: " + name);
        }

        // Windows device names are not portable, even if the catalog is queried on Linux.
        var baseName = name.Split('.')[0];
        var reserved = baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                       baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                       baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                       baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                       (baseName.Length == 4 &&
                        (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                         baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                        baseName[3] >= '1' && baseName[3] <= '9');
        if (reserved)
        {
            throw new InvalidDataException("Reserved plugin folder name: " + name);
        }
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c =>
            invalid.Contains(c) || "/\\:*?\"<>|".Contains(c) ? '_' : c).ToArray())
            .Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(cleaned) ? "Plugin" : cleaned;
    }
}
