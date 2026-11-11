using Nikse.SubtitleEdit.Logic.Config;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Nikse.SubtitleEdit.Logic.Plugins;

public class PluginCatalog : IPluginCatalog
{
    public IReadOnlyList<InstalledPlugin> GetPlugins() => GetPluginsInFolder(Se.PluginsFolder);

    // Tests use an isolated plugin folder; staging/backup folders must never be
    // treated as installed plugins while an install/upgrade is in progress.
    internal static IReadOnlyList<InstalledPlugin> GetPluginsInFolder(string root)
    {
        var result = new List<InstalledPlugin>();
        if (!Directory.Exists(root))
        {
            return result;
        }

        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(folder);
            if (name.Equals("Data", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(".tmp-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(".bak-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var manifestPath = Path.Combine(folder, PluginConstants.ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                using var stream = File.OpenRead(manifestPath);
                var manifest = JsonSerializer.Deserialize(stream, PluginJsonContext.Default.PluginManifest);
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Name))
                {
                    continue;
                }

                var usesDotnet = string.Equals(manifest.Runtime, PluginConstants.RuntimeDotnet, StringComparison.OrdinalIgnoreCase);
                var launchPath = ResolveLaunchPath(folder, manifest, usesDotnet);

                result.Add(new InstalledPlugin
                {
                    Manifest = manifest,
                    FolderPath = folder,
                    ManifestPath = manifestPath,
                    LaunchPath = launchPath,
                    UsesDotnetRuntime = usesDotnet,
                });
            }
            catch (Exception exception)
            {
                Se.LogError(exception, "Failed to read plugin manifest: " + manifestPath);
            }
        }

        return result;
    }

    private static string? ResolveLaunchPath(string folder, PluginManifest manifest, bool usesDotnet)
    {
        if (usesDotnet)
        {
            if (string.IsNullOrWhiteSpace(manifest.Entry))
            {
                return null;
            }

            var entryPath = Path.Combine(folder, manifest.Entry);
            return File.Exists(entryPath) ? entryPath : null;
        }

        var executable = GetExecutableForCurrentOs(manifest.Executables);
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        var path = Path.Combine(folder, executable);
        return File.Exists(path) ? path : null;
    }

    private static string? GetExecutableForCurrentOs(PluginExecutables? executables)
    {
        if (executables == null)
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            return executables.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return executables.Macos;
        }

        return executables.Linux;
    }
}
