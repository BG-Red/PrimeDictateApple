# Agent notes for PrimeDictate

This file orients coding agents and future maintainers. It is not an end-user manual; see [README.md](README.md) for that.

## Purpose

**PrimeDictate** is a .NET 8 **Windows** app with a **WPF tray shell** and **first-run onboarding**. It:

1. Listens for a **global** hotkey (`Ctrl+Shift+Space` / SharpHook) to start and stop capture.
2. Records from the **default** Windows input device using **WASAPI** (NAudio `WasapiCapture`), normalizing to **16 kHz, 16-bit, mono** PCM.
3. Runs local **ONNX** speech models through **sherpa-onnx** for live preview and final transcription.
4. Shows live transcript hypotheses in a non-activating WPF overlay, then injects the final result with **SharpHook** `EventSimulator.SimulateTextEntry` (unicode text simulation), not clipboard + synthetic paste. Optional coding mode sends `Enter` after a successful final commit.

## Layout

| File / folder | Role |
|------|------|
| `Program.cs` | `Main`, hotkey listener, `DictationController` toggle, `DefaultMicrophoneRecorder`, `PcmAudioBuffer`. |
| `ModelStorage.cs` | Shared managed model root under `%LocalAppData%\PrimeDictate\models`. |
| `WhisperModelCatalog.cs` | Whisper ONNX catalog, folder validation, download, and archive extraction. |
| `ParakeetModelCatalog.cs` | Parakeet ONNX catalog, folder validation, download, and archive extraction. |
| `MoonshineModelCatalog.cs` | Moonshine ONNX catalog, folder validation, download, and archive extraction. |
| `TranscriptionEngines.cs` | Shared transcription abstraction; Whisper, Parakeet, and Moonshine ONNX engines with lazy runtime/model load. |
| `WhisperTextInjectionPipeline.cs` | Transcription orchestration, logging, and final-only text injection. |
| `WindowsInputHelpers.cs` | Foreground-window guard for final injection and optional Windows Mouse Sonar pulse. |
| `TranscriptionOverlayWindow.xaml` | Non-activating live transcript overlay; placement is user-configurable. |
| `PrimeDictate.csproj` | Target `net8.0`; NAudio, SharpHook, sherpa-onnx. |
| `Directory.Build.props` | Shared assembly/file `Version` (installers read this too). |
| `scripts/Publish-Windows.ps1` | Self-contained `win-x64` publish to `artifacts/win-x64/publish`. |
| `scripts/Build-Installers.ps1` | Publishes then builds WiX `.wixproj` targets (NuGet `WixToolset.Sdk`) to MSIs in `artifacts/installer`. |
| `installer/wix/` | WiX: offline MSI bundles the default Whisper ONNX files + Start Menu shortcut; online MSI uses `WixQuietExec` to run `DownloadModel.cmd`; `Branding.wxs` + `PrimeDictate.ico` for ARP/exe icon. |

## Conventions to preserve

- **ONNX model folders**: Whisper folders contain encoder ONNX, decoder ONNX, and tokens; Parakeet/Moonshine have their own required ONNX file sets in their catalogs.
- **Native / unmanaged**: Prefer `await using` and explicit disposal paths; do not add redundant `try`/`catch` unless there is a clear recovery story.
- **Hotkey handler**: The hook runs on SharpHook's thread; work is offloaded with `Task.Run` and `await` the dictation path carefully to avoid re-entrancy issues. `DictationController` uses a `SemaphoreSlim` for toggle mutual exclusion.
- **Text injection**: **Do not** reintroduce "set clipboard + simulate paste + immediately restore old clipboard" without solving async paste delivery (delay, flush, or full clipboard snapshot/restore). The vetted baseline is final-only target `SimulateTextEntry` (see product README for rationale).
- **Editor stability**: Live updates belong in the overlay, not in the target editor. Do not reintroduce live backspace/re-type correction into the focused app without a robust target/caret/completion strategy.
- **Coding mode Enter**: The optional Enter key is sent only after final text injection succeeds and the foreground-window guard passes.
- **Model path**: Keep model-folder validation and download layout in the model catalog classes; do not scatter model filename assumptions through UI or engine code.

## Dependencies (NuGet)

- **org.k2fsa.sherpa.onnx** for ONNX speech recognition runtimes and managed bindings.
- **NAudio** for capture and resampling.
- **SharpHook** for the global hook and `EventSimulator`.

**TextCopy** is not used; do not add it back unless you implement a clipboard strategy that is demonstrably free of the paste/restore race.

## Extension points (expected evolution)

- **Hotkey**: Change `IsDictationHotkey` in `GlobalHotkeyListener` (`Program.cs`); key codes in `SharpHook.Data.KeyCode`.
- **Transcription engines**: Add new local model runtimes behind `ITranscriptionEngine` in `TranscriptionEngines.cs`; keep text injection out of engine implementations.
- **Whisper options**: Whisper uses sherpa-onnx `OfflineRecognizerConfig.ModelConfig.Whisper`; add provider/thread/language controls there when needed.
- **Non-Windows audio**: `DefaultMicrophoneRecorder` is Windows-centric (`WasapiCapture`); a cross-platform build would need an abstraction and platform-specific capture.

## Shipped shell + onboarding notes

The tray/onboarding milestone is now implemented:

1. **Host process**: WPF tray host (`App.xaml`) with notification icon, Settings/Exit menu, live transcript overlay, and Ready/Listening/Processing tooltip state.
2. **User settings**: Persisted under `%LocalAppData%\PrimeDictate\settings.json`; loaded at startup and applied to `GlobalHotkeyListener`.
3. **First run**: Missing/incomplete settings show `SettingsWindow` before normal tray-only behavior.
4. **WiX launch option**: Offline and online packages include finish-page launch checkbox to start `PrimeDictate.exe`.
5. **Preserved invariants**: hook-thread offload, foreground-window guard, overlay-only live preview, ONNX model-folder validation, and final-only target `SimulateTextEntry` baseline remain intact.

## Build and test hints

- **Windows installers**: See [installer/README.md](installer/README.md). Offline MSIs require `models\whisper\sherpa-onnx-whisper-base.en\base.en-encoder.int8.onnx`, `base.en-decoder.int8.onnx`, and `base.en-tokens.txt` on the maintainer machine. Online MSIs download and extract that same ONNX model after install.
- Run from the **repository root** so `./models/whisper/<model folder>` can be discovered during development when models are staged in the repo-local `models` tree.
- A running `PrimeDictate.exe` from `dotnet run` can **lock the apphost**; stop the process if `MSB3021` / copy-to-output fails.
- Linter: project should build with **0 warnings** under default SDK analysis when possible; platform-specific API use should stay behind `OperatingSystem` checks or documented trade-offs.

## Out of scope (unless explicitly requested)

- Cloud APIs, always-on online STT, or shipping large model blobs inside the repository; document/download them instead.
