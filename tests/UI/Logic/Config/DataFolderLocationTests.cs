using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Features.Video.SpeechToText.Engines;
using Nikse.SubtitleEdit.Features.Video.TextToSpeech.Engines;
using Nikse.SubtitleEdit.UiLogic.AudioToText;

namespace UITests.Logic.Config;

/// <summary>
/// Pins where user data lives. The portable check used to rely on
/// <c>ExePath.StartsWith(programFilesX86)</c> with an empty path off Windows, which is always
/// true - so every non-Windows install landed in the per-user folder by accident. That outcome
/// is the intended one and is now explicit; these tests exist because "correcting" the check
/// into real portable detection would move the data folder out from under existing macOS and
/// Linux users, losing their settings, dictionaries and themes.
/// </summary>
public class DataFolderLocationTests
{
    [Fact]
    public void NonWindows_IsNeverPortable_AndUsesThePerUserDataFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Subtitle Edit");

        Assert.False(Se.IsPortable);
        Assert.Equal(expected, Se.DataFolder);
    }

    /// <summary>
    /// Off Windows <c>GetFolderPath(ApplicationData)</c> returns an empty string when the folder
    /// does not exist yet, so <c>Path.Combine(..., "Subtitle Edit")</c> used to yield the bare
    /// relative "Subtitle Edit" - settings, dictionaries and themes then landed in whatever
    /// directory the app happened to be started from. Anything absolute beats that.
    /// </summary>
    [Fact]
    public void MissingApplicationDataFolder_FallsBackToTheExeFolder_NeverARelativePath()
    {
        var resolved = Se.ResolveDataFolder(isPortable: false, Se.ExePath, appDataFolder: string.Empty);

        Assert.Equal(Se.ExePath, resolved);
        Assert.True(Path.IsPathRooted(resolved), $"\"{resolved}\" is not an absolute path.");
    }

    /// <summary>
    /// Pins the lookup itself, where both failure modes live: <c>SpecialFolderOption.None</c>
    /// returns an empty string for a folder that does not exist yet - the empty string that made
    /// DataFolder relative in the first place - while <c>SpecialFolderOption.Create</c> creates
    /// the folder here, and throws when it cannot, which inside Se's static constructor kills the
    /// app at startup. DoNotVerify does neither.
    /// <para>
    /// Linux only, because that is where the bug lives: macOS resolves the folder through
    /// NSSearchPath and ignores XDG_CONFIG_HOME, and on Windows %AppData% always exists.
    /// </para>
    /// </summary>
    [Fact]
    public void ApplicationDataLookup_ReturnsAMissingFolder_WithoutEmptyingItOrCreatingIt()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var missing = Path.Combine(Path.GetTempPath(), "se-missing-appdata-" + Guid.NewGuid().ToString("N"));
        var original = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", missing);

            var folder = Se.GetApplicationDataFolder();

            Assert.Equal(missing, folder);
            Assert.False(Directory.Exists(folder), $"\"{folder}\" was created by merely looking it up.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", original);
            if (Directory.Exists(missing))
            {
                Directory.Delete(missing, recursive: true);
            }
        }
    }

    /// <summary>
    /// Every runtime folder hangs off DataFolder, so pinning that pins the rest. Guards against
    /// a stray absolute or working-directory-relative path creeping back in.
    /// </summary>
    [Fact]
    public void RuntimeFolders_AreAllUnderTheDataFolder()
    {
        string[] folders = [Se.ThemesFolder, Se.TranslationFolder, Se.OcrFolder, Se.SevenZipFolder];

        foreach (var folder in folders)
        {
            Assert.True(
                Path.IsPathRooted(folder),
                $"\"{folder}\" is not an absolute path - it would resolve against the working directory.");
            Assert.StartsWith(Se.DataFolder, folder, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EmptyModelsFolder_PreservesLegacyLocations()
    {
        var original = Se.Settings.General.ModelsFolder;
        var originalModelsDirectory = Configuration.ModelsDirectory;
        try
        {
            Se.Settings.General.ModelsFolder = string.Empty;
            Configuration.ModelsDirectory = string.Empty;

            Assert.Equal(Se.DataFolder, Se.ModelsFolder);
            Assert.False(Se.HasCustomModelsFolder);
            Assert.Equal(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "whisper"),
                new WhisperModel().ModelFolder);
            Assert.Equal(Path.Combine(Se.TextToSpeechFolder, "Piper"), Se.PiperModelsFolder);
        }
        finally
        {
            Se.Settings.General.ModelsFolder = original;
            Configuration.ModelsDirectory = originalModelsDirectory;
        }
    }

    [Fact]
    public void DataFolderWithTrailingSeparator_RemainsLegacyLayout()
    {
        var original = Se.Settings.General.ModelsFolder;
        try
        {
            Se.Settings.General.ModelsFolder = Se.DataFolder + Path.DirectorySeparatorChar;

            Assert.Equal(Path.TrimEndingDirectorySeparator(Se.DataFolder), Se.ModelsFolder);
            Assert.False(Se.HasCustomModelsFolder);
        }
        finally
        {
            Se.Settings.General.ModelsFolder = original;
        }
    }

    [Fact]
    public void WindowsDataFolderComparison_IsCaseInsensitive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var original = Se.Settings.General.ModelsFolder;
        try
        {
            Se.Settings.General.ModelsFolder = Se.DataFolder.ToUpperInvariant();

            Assert.False(Se.HasCustomModelsFolder);
        }
        finally
        {
            Se.Settings.General.ModelsFolder = original;
        }
    }

    [Fact]
    public void RelativeModelsFolder_FallsBackToDataFolder()
    {
        var original = Se.Settings.General.ModelsFolder;
        try
        {
            Se.Settings.General.ModelsFolder = Path.Combine("relative", "models");

            Assert.Equal(Se.DataFolder, Se.ModelsFolder);
            Assert.False(Se.HasCustomModelsFolder);
        }
        finally
        {
            Se.Settings.General.ModelsFolder = original;
        }
    }

    [Fact]
    public void CustomModelsFolder_UsesTheSelectedRootWithoutChangingTheAppDataFolder()
    {
        var original = Se.Settings.General.ModelsFolder;
        var originalModelsDirectory = Configuration.ModelsDirectory;
        var selected = Path.Combine(Path.GetTempPath(), "subtitle-edit-models-" + Guid.NewGuid().ToString("N"));
        try
        {
            Se.Settings.General.ModelsFolder = selected;
            Configuration.ModelsDirectory = selected;

            Assert.Equal(Path.GetFullPath(selected), Se.ModelsFolder);
            Assert.True(Se.HasCustomModelsFolder);
            Assert.Equal(Se.DataFolder, Path.GetDirectoryName(Se.GetErrorLogFilePath()));
            Assert.Equal(Path.Combine(selected, "CrispASR", "models"), Se.CrispAsrModelsFolder);
            Assert.Equal(
                Path.Combine(selected, "SpeechToText", "whisper"),
                new WhisperModel().ModelFolder);
            Assert.Equal(
                Path.Combine(selected, "TextToSpeech", "Piper", "models"),
                Se.PiperModelsFolder);
        }
        finally
        {
            Se.Settings.General.ModelsFolder = original;
            Configuration.ModelsDirectory = originalModelsDirectory;
        }
    }

    [Fact]
    public void CustomModelsFolder_RoutesAllCurrentAudioCppTtsModelsUnderSelectedRoot()
    {
        var original = Se.Settings.General.ModelsFolder;
        var originalModelsDirectory = Configuration.ModelsDirectory;
        var selected = Path.Combine(Path.GetTempPath(), "subtitle-edit-audiocpp-models-" + Guid.NewGuid().ToString("N"));
        try
        {
            Se.Settings.General.ModelsFolder = selected;
            Configuration.ModelsDirectory = selected;

            Assert.Equal(
                Path.Combine(selected, "audio.cpp", "models", "IndexTTS2.5-GGUF"),
                IndexTts25AudioCpp.GetSetModelsFolder());
            Assert.Equal(
                Path.Combine(selected, "audio.cpp", "models", "FireRedTTS3-Base-GGUF"),
                FireRedTts3AudioCpp.GetSetModelsFolder());
            Assert.Equal(
                Path.Combine(selected, "audio.cpp", "models", "Fish-Audio-S2-Pro-GGUF"),
                FishTtsAudioCpp.GetSetModelsFolder());
            Assert.Equal(
                Path.Combine(selected, "audio.cpp", "models", "Higgs-Audio-v3-TTS-4B-GGUF"),
                HiggsTtsAudioCpp.GetSetModelsFolder());
        }
        finally
        {
            Se.Settings.General.ModelsFolder = original;
            Configuration.ModelsDirectory = originalModelsDirectory;
            if (Directory.Exists(selected))
            {
                Directory.Delete(selected, recursive: true);
            }
        }
    }

    [Fact]
    public void CustomModelsFolder_ConfiguresWhisperXHuggingFaceCacheUnderSelectedRoot()
    {
        var original = Se.Settings.General.ModelsFolder;
        var selected = Path.Combine(Path.GetTempPath(), "subtitle-edit-whisperx-models-" + Guid.NewGuid().ToString("N"));
        try
        {
            Se.Settings.General.ModelsFolder = selected;
            var startInfo = new System.Diagnostics.ProcessStartInfo();

            WhisperEngineWhisperX.ConfigureModelEnvironment(startInfo);

            Assert.Equal(
                Path.Combine(selected, "SpeechToText", "HuggingFace"),
                startInfo.EnvironmentVariables["HF_HOME"]);
        }
        finally
        {
            Se.Settings.General.ModelsFolder = original;
        }
    }

    [Fact]
    public void LegacyModelsLayout_DoesNotOverrideCallerWhisperXHuggingFaceCache()
    {
        var original = Se.Settings.General.ModelsFolder;
        try
        {
            Se.Settings.General.ModelsFolder = string.Empty;
            var startInfo = new System.Diagnostics.ProcessStartInfo();
            startInfo.EnvironmentVariables["HF_HOME"] = "keep-hf-home";

            WhisperEngineWhisperX.ConfigureModelEnvironment(startInfo);

            Assert.Equal("keep-hf-home", startInfo.EnvironmentVariables["HF_HOME"]);
        }
        finally
        {
            Se.Settings.General.ModelsFolder = original;
        }
    }

    [Fact]
    public void CustomModelsFolder_ConfiguresCrispAsrAutoDownloadsUnderSelectedRoot()
    {
        var original = Se.Settings.General.ModelsFolder;
        var originalModelsDirectory = Configuration.ModelsDirectory;
        var selected = Path.Combine(Path.GetTempPath(), "subtitle-edit-crispasr-models-" + Guid.NewGuid().ToString("N"));
        try
        {
            Se.Settings.General.ModelsFolder = selected;
            Configuration.ModelsDirectory = selected;
            var startInfo = new System.Diagnostics.ProcessStartInfo();

            CrispAsrEngineBase.ConfigureModelEnvironment(startInfo);

            var expected = Path.Combine(selected, "CrispASR", "models");
            Assert.Equal(expected, CrispAsrEngineBase.AutoDownloadModelsFolder);
            Assert.Equal(expected, startInfo.EnvironmentVariables["CRISPASR_MODELS_DIR"]);
            Assert.Equal(expected, startInfo.EnvironmentVariables["CRISPASR_CACHE_DIR"]);
            Assert.True(Directory.Exists(expected));
        }
        finally
        {
            Se.Settings.General.ModelsFolder = original;
            Configuration.ModelsDirectory = originalModelsDirectory;
            if (Directory.Exists(selected))
            {
                Directory.Delete(selected, recursive: true);
            }
        }
    }

    [Fact]
    public void LegacyModelsLayout_ReusesCrispAsrEnvironmentPrecedence()
    {
        var originalModelsFolder = Se.Settings.General.ModelsFolder;
        var originalModelsDirectory = Configuration.ModelsDirectory;
        var originalCacheEnvironment = Environment.GetEnvironmentVariable("CRISPASR_CACHE_DIR");
        var originalModelsEnvironment = Environment.GetEnvironmentVariable("CRISPASR_MODELS_DIR");
        try
        {
            Se.Settings.General.ModelsFolder = string.Empty;
            Configuration.ModelsDirectory = string.Empty;
            Environment.SetEnvironmentVariable("CRISPASR_MODELS_DIR", "models-root");
            Environment.SetEnvironmentVariable("CRISPASR_CACHE_DIR", "cache-root");

            Assert.Equal("cache-root", CrispAsrEngineBase.AutoDownloadModelsFolder);

            Environment.SetEnvironmentVariable("CRISPASR_CACHE_DIR", null);

            Assert.Equal("models-root", CrispAsrEngineBase.AutoDownloadModelsFolder);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CRISPASR_CACHE_DIR", originalCacheEnvironment);
            Environment.SetEnvironmentVariable("CRISPASR_MODELS_DIR", originalModelsEnvironment);
            Se.Settings.General.ModelsFolder = originalModelsFolder;
            Configuration.ModelsDirectory = originalModelsDirectory;
        }
    }

    [Fact]
    public void LegacyModelsLayout_DoesNotOverrideCallerCrispAsrEnvironment()
    {
        var original = Se.Settings.General.ModelsFolder;
        var originalModelsDirectory = Configuration.ModelsDirectory;
        try
        {
            Se.Settings.General.ModelsFolder = string.Empty;
            Configuration.ModelsDirectory = string.Empty;
            var startInfo = new System.Diagnostics.ProcessStartInfo();
            startInfo.EnvironmentVariables["CRISPASR_MODELS_DIR"] = "keep-models";
            startInfo.EnvironmentVariables["CRISPASR_CACHE_DIR"] = "keep-cache";

            CrispAsrEngineBase.ConfigureModelEnvironment(startInfo);

            Assert.Equal("keep-models", startInfo.EnvironmentVariables["CRISPASR_MODELS_DIR"]);
            Assert.Equal("keep-cache", startInfo.EnvironmentVariables["CRISPASR_CACHE_DIR"]);
        }
        finally
        {
            Se.Settings.General.ModelsFolder = original;
            Configuration.ModelsDirectory = originalModelsDirectory;
        }
    }
}
