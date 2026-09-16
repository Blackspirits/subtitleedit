# Issue #14024 - configurable AI models folder

## Status

Ported onto the current Subtitle Edit tree for audit. The original implementation is extended here so CrispASR's own auto-download cache follows the selected models root as well.

The original request is [Subtitle Edit issue #14024](https://github.com/SubtitleEdit/subtitleedit/issues/14024): macOS users with a small internal disk need the large downloaded AI models to live on an external SSD, without moving the whole Subtitle Edit application-data directory.

## Problem

Before this change, Subtitle Edit had several independent model locations:

| Model family | Previous location | Why this was a problem |
| --- | --- | --- |
| OpenAI Whisper Python | `~/.cache/whisper` | The cache was outside Subtitle Edit's settings and had no in-app location setting. |
| Hugging Face / CTranslate2 | `~/.cache/huggingface/hub` | Large model snapshots accumulated on the internal disk. |
| Subtitle Edit Whisper engines | under the app data folder | These were tied to the normal application-data location. |
| CrispASR and its TTS backends | `<data>/CrispASR/models` | The executable and model files were coupled to one root. |
| llama.cpp | `<data>/llama.cpp/models` | Moving the whole data folder was the only practical workaround. |
| Paddle OCR, CrispEmbed, and Tesseract | OCR-specific subfolders | OCR model downloads also consumed internal storage. |
| audio.cpp and several C++ TTS engines | engine-specific `models` folders | Each engine had to be moved independently. |

The workaround described in the issue was to symlink the complete `Subtitle Edit` application-data directory. That also moves settings, logs, dictionaries, themes, and downloaded tools, which is broader than necessary.

## Design

The setting is an optional model root, not a replacement for the application-data root.

- An empty value preserves every previous path.
- A selected path is normalized to an absolute path.
- Executables and normal Subtitle Edit state remain in the normal application-data folder.
- Model subfolders retain stable family-specific names below the selected root.
- The setting is persisted in `Settings.json` as `General.ModelsFolder`.
- Existing files are not copied or deleted automatically. This avoids destructive migration and makes changing the setting reversible. Users can copy the existing model folders to the corresponding subfolders on the new disk before using the engines.
- Invalid hand-edited paths fall back to the historical locations instead of preventing startup.

## User-facing behavior

Options > General now includes **AI models folder** with a folder picker. Leave it empty to use the old locations. Select a directory such as `/Volumes/AI-Models/Subtitle Edit` to put future model downloads there.

The selected root is used after settings are saved. Python-based engines receive cache environment variables at process launch:

- OpenAI Whisper receives `XDG_CACHE_HOME=<selected root>/SpeechToText`, which makes its normal `whisper` cache resolve under the selected root.
- CTranslate2 and WhisperX receive `HF_HOME=<selected root>/SpeechToText/HuggingFace`, so their Hugging Face model caches resolve under the selected root.
- CrispASR receives `CRISPASR_MODELS_DIR` and `CRISPASR_CACHE_DIR` pointing at `<selected root>/CrispASR/models`, so native `--auto-download` assets do not escape back to `~/.cache/crispasr`.

## Implementation details

### Configuration and persistence

- `src/ui/Logic/Config/SeGeneral.cs` adds `ModelsFolder`, defaulting to an empty string.
- `src/ui/Logic/Config/Se.cs` adds normalized `ModelsFolder`, `HasCustomModelsFolder`, and model path helpers.
- `src/libse/Common/Configuration.cs` adds the shared `ModelsDirectory` bridge and `ResolveModelsFolder`, so `libuilogic` can use the setting without depending on the UI assembly.
- `Se.UpdateLibSeSettings()` synchronizes the selected root into the shared configuration bridge.
- `Se.SaveSettings()` refreshes the llama.cpp model override after a settings change.

### Settings UI and localization

- `src/ui/Features/Options/Settings/SettingsPage.cs` adds the folder textbox and browse button.
- `src/ui/Features/Options/Settings/SettingsViewModel.cs` loads, browses, trims, and saves the value.
- `src/ui/Logic/Config/Language/Options/LanguageSettings.cs` and `src/ui/Assets/Languages/English.json` add the **AI models folder** label. The language property has an English initializer so older translation files remain usable.

### Model families covered

- OpenAI Whisper, CTranslate2, and WhisperX external model caches.
- Whisper.cpp and Const-me model folders, including the C++/cuBLAS/Vulkan engine wrappers.
- Purfview Faster Whisper XXL's `_models` folder.
- All current CrispASR speech-to-text backends and CrispASR-backed TTS model folders.
- Qwen3 ASR C++ model files.
- Qwen3 TTS C++, Kokoro TTS C++, and OmniVoice TTS C++ model folders.
- All current audio.cpp TTS model families: IndexTTS 2.5, FireRedTTS3, Fish Audio S2 Pro, and Higgs Audio v3.
- Piper voice-model `.onnx` / config files, while the Piper runtime and voice catalog remain in application data.
- llama.cpp model files, while its server executable remains in the normal data folder.
- PaddleOCR models, CrispEmbed models, and Tesseract `tessdata`.

The path substitutions are intentionally centralized in `Se` so a future engine can opt into the same root without duplicating settings parsing or migration logic.

## Backward compatibility and migration

No setting means no behavior change. The legacy paths are still returned exactly when `General.ModelsFolder` is empty, and the tests pin this behavior.

Changing the setting does not move existing data. This is deliberate:

1. Subtitle Edit cannot safely assume that every folder is writable, mounted, or large enough for a copy.
2. A move could be interrupted and leave a partial model.
3. The user may want to keep models on both disks temporarily.

For a manual migration, close Subtitle Edit, copy the old model subfolder to the matching path below the new root, select the root in Options, save, and verify the engine's model list before deleting the old copy.

## Verification

The focused test command was:

```text
AVALONIA_TELEMETRY_OPTOUT=1 dotnet test tests/UI/UITests.csproj --no-restore --filter FullyQualifiedName~DataFolderLocationTests
```

The focused regression suite verifies:

- empty settings preserve historical model locations, including Piper;
- relative hand-edited roots fail closed to the historical layout;
- a custom absolute root leaves normal application data unchanged;
- all current audio.cpp TTS model families resolve below the custom root;
- Piper model files resolve below the custom root while its runtime stays in application data;
- WhisperX routes Hugging Face downloads through the custom root and preserves caller environment in legacy mode;
- CrispASR native auto-downloads use the custom root;
- legacy CrispASR cache reuse follows `CRISPASR_CACHE_DIR` → `CRISPASR_MODELS_DIR` → the historical cache;
- legacy mode does not overwrite caller-provided CrispASR environment.

Full build/test results are recorded by CI for the audit branch. No production model is downloaded by these path tests.

## Not included in this slice

- Automatic copy/move of existing model files.
- Third-party caches that are hard-coded inside external tools and are not controlled by an explicit model-directory argument. CrispASR is no longer in this exception: its documented `CRISPASR_MODELS_DIR` / `CRISPASR_CACHE_DIR` overrides are applied when a custom root is selected.
- A separate per-engine UI. The request is satisfied with one root, while preserving each engine's existing subfolder layout.

## Follow-up options

1. Add an explicit “Move existing models” wizard with free-space checks and resumable copy.
2. Add a “Show model folder” action beside the setting.
3. Audit newly added third-party engines for undocumented internal caches and add environment/argument overrides where upstream supports them.