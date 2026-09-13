using Nikse.SubtitleEdit.Features.Video.TextToSpeech.Voices;
using Nikse.SubtitleEdit.Logic.Config;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Nikse.SubtitleEdit.Features.Video.TextToSpeech.Engines;

/// <summary>
/// Renames a user-imported cloning voice on disk. Every clone engine lists its voices straight
/// from its voices folder - one reference WAV per voice, named after the file with underscores
/// shown as spaces - so a rename is a file move: the WAV, any sidecar beside it with the same base
/// name (the <c>.txt</c> transcript, engine JSON, ...) and the cached
/// <see cref="CloneReferenceTail"/> copy, which is keyed by file name and would otherwise be left
/// behind as an orphan.
/// </summary>
public static class VoiceFileRename
{
    private sealed class RenameMove(string source, string target, string temp)
    {
        public string Source { get; } = source;
        public string Target { get; } = target;
        public string Temp { get; } = temp;
    }

    /// <summary>
    /// The reference recording <paramref name="voice"/> clones from, or null when the voice is
    /// not a renamable file-backed clone: an engine preset, the "Default" speaker, the per-line
    /// clone marker, or a reference staged for a single line of one run.
    /// </summary>
    public static string? GetReferenceFilePath(Voice voice)
    {
        var filePath = voice.EngineVoice switch
        {
            ChatterboxVoice v => v.FilePath,
            Confucius4TtsVoice v => v.FilePath,
            CosyVoice3Voice v => v.FilePath,
            DotsTtsVoice v => v.FilePath,
            F5TtsVoice v => v.FilePath,
            IndexTtsVoice v => v.FilePath,
            MossTtsVoice v => v.FilePath,
            OmniVoice v => v.FilePath,
            OmniVoiceCrispAsrVoice v => v.FilePath,
            PocketTtsVoice v => v.FilePath,
            Qwen3TtsVoice v => v.FilePath,
            VibeVoice v => v.FilePath,
            VoxCPM2Voice v => v.FilePath,
            ZonosTtsVoice v => v.FilePath,
            _ => string.Empty,
        };

        if (string.IsNullOrEmpty(filePath) || PerLineReferenceStaging.IsStaged(filePath))
        {
            return null;
        }

        return filePath;
    }

    public static bool CanRename(Voice? voice) =>
        voice != null && GetReferenceFilePath(voice) is { } path && File.Exists(path);

    /// <summary>
    /// Deletes the voice's reference WAV, its sidecars and the cached prepared copy. Returns
    /// false with <paramref name="error"/> set when the voice is not a file-backed clone or the
    /// delete failed.
    /// </summary>
    public static bool Delete(Voice voice, out string error)
    {
        error = string.Empty;
        var fileName = GetReferenceFilePath(voice);
        if (fileName == null || !File.Exists(fileName))
        {
            error = "Voice file not found";
            return false;
        }

        try
        {
            var folder = Path.GetDirectoryName(fileName) ?? string.Empty;
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            foreach (var file in Directory.GetFiles(folder, baseName + ".*"))
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(file), baseName, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }

            var prepared = CloneReferenceTail.GetPreparedFileName(fileName);
            foreach (var stale in new[] { prepared, prepared + ".stamp" }.Where(File.Exists))
            {
                File.Delete(stale);
            }

            Se.WriteToolsLog($"TTS voice deleted: '{fileName}'");
            return true;
        }
        catch (Exception ex)
        {
            Se.LogError(ex, $"Deleting TTS voice '{fileName}' failed");
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Moves the voice's files to <paramref name="newName"/>. Returns the new reference file
    /// name, or null with <paramref name="error"/> set when nothing was changed.
    /// </summary>
    public static string? Rename(Voice voice, string newName, out string error)
    {
        error = string.Empty;
        var oldFileName = GetReferenceFilePath(voice);
        if (oldFileName == null || !File.Exists(oldFileName))
        {
            error = "Voice file not found";
            return null;
        }

        newName = newName.Trim();
        if (string.IsNullOrEmpty(newName))
        {
            error = "Name is empty";
            return null;
        }

        // The engines show '_' as ' ', so store spaces as underscores to round-trip the name.
        var newBaseName = newName.Replace(' ', '_');
        if (newBaseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || newBaseName.Contains("..") ||
            newBaseName.StartsWith(PerLineReferenceStaging.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            error = "Name contains invalid characters";
            return null;
        }

        var folder = Path.GetDirectoryName(oldFileName) ?? string.Empty;
        var oldBaseName = Path.GetFileNameWithoutExtension(oldFileName);
        var extension = Path.GetExtension(oldFileName);
        var newFileName = Path.Combine(folder, newBaseName + extension);
        if (string.Equals(oldBaseName, newBaseName, StringComparison.Ordinal))
        {
            return oldFileName;
        }

        var sourceFiles = Directory.GetFiles(folder, oldBaseName + ".*")
            .Where(file => string.Equals(
                Path.GetFileNameWithoutExtension(file),
                oldBaseName,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!sourceFiles.Any(file => string.Equals(file, oldFileName, StringComparison.OrdinalIgnoreCase)))
        {
            sourceFiles.Add(oldFileName);
        }

        var moves = sourceFiles
            .Select(source => new RenameMove(
                source,
                Path.Combine(folder, newBaseName + Path.GetExtension(source)),
                Path.Combine(folder, $".se-voice-rename-{Guid.NewGuid():N}.tmp")))
            .ToList();

        var staged = new List<RenameMove>();
        var published = new List<RenameMove>();
        try
        {
            // Stage every source first. Besides making rollback possible, this makes a case-only
            // rename portable: on a case-insensitive file system the destination stops existing
            // once the source has been staged, while on a case-sensitive file system a distinct
            // destination with the other casing remains and is detected below.
            foreach (var move in moves)
            {
                File.Move(move.Source, move.Temp);
                staged.Add(move);
            }

            foreach (var move in moves)
            {
                if (File.Exists(move.Target))
                {
                    throw new IOException($"A voice file named '{Path.GetFileName(move.Target)}' already exists");
                }
            }

            foreach (var move in moves)
            {
                File.Move(move.Temp, move.Target);
                published.Add(move);
            }
        }
        catch (Exception ex)
        {
            RollbackRename(staged, published);
            Se.LogError(ex, $"Renaming TTS voice '{oldFileName}' to '{newFileName}' failed");
            error = ex.Message;
            return null;
        }

        // The prepared copy is keyed on the reference's file name; the next synthesis makes a
        // fresh one under the new name. Cache cleanup is best-effort and must not roll back an
        // otherwise successful rename.
        var prepared = CloneReferenceTail.GetPreparedFileName(oldFileName);
        foreach (var stale in new[] { prepared, prepared + ".stamp" }.Where(File.Exists))
        {
            try
            {
                File.Delete(stale);
            }
            catch (Exception ex)
            {
                Se.LogError(ex, $"Removing stale TTS voice cache '{stale}' failed");
            }
        }

        Se.WriteToolsLog($"TTS voice renamed: '{oldFileName}' -> '{newFileName}'");
        return newFileName;
    }

    private static void RollbackRename(List<RenameMove> staged, List<RenameMove> published)
    {
        // Published case-only paths can alias their original source on case-insensitive file
        // systems. Move them through a unique temporary path so restoring the original casing is
        // reliable without ever overwriting another voice.
        for (var i = published.Count - 1; i >= 0; i--)
        {
            var move = published[i];
            if (!File.Exists(move.Target))
            {
                continue;
            }

            var rollbackTemp = Path.Combine(
                Path.GetDirectoryName(move.Source) ?? string.Empty,
                $".se-voice-rename-rollback-{Guid.NewGuid():N}.tmp");
            try
            {
                File.Move(move.Target, rollbackTemp);
                File.Move(rollbackTemp, move.Source);
            }
            catch (Exception ex)
            {
                Se.LogError(ex, $"Rolling back TTS voice rename '{move.Target}' -> '{move.Source}' failed");
            }
        }

        for (var i = staged.Count - 1; i >= 0; i--)
        {
            var move = staged[i];
            if (!File.Exists(move.Temp))
            {
                continue;
            }

            try
            {
                File.Move(move.Temp, move.Source);
            }
            catch (Exception ex)
            {
                Se.LogError(ex, $"Restoring staged TTS voice file '{move.Source}' failed");
            }
        }
    }
}
