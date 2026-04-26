# Agent notes for PrimeDictate

This file orients coding agents and future maintainers. It is not an end-user manual; see [README.md](README.md) for that.

## Purpose

**PrimeDictate** is a .NET 8 **Windows** app with a **WPF tray shell** and **first-run onboarding**. It:

1. Listens for a **global** hotkey (`Ctrl+Shift+Space` / SharpHook) to start and stop capture.
2. Records from the **default** Windows input device using **WASAPI** (NAudio `WasapiCapture`), normalizing to **16 kHz, 16-bit, mono** PCM.
3. Feeds a **WAV** stream in memory to **Whisper.net** (whisper.cpp) for transcription.
4. Injects the result with **SharpHook** `EventSimulator.SimulateTextEntry` (unicode text simulation), not clipboard + synthetic paste.

## Layout

| File / folder | Role |
|------|------|
| `Program.cs` | `Main`, hotkey listener, `DictationController` toggle, `DefaultMicrophoneRecorder`, `PcmAudioBuffer`. |
| `WhisperTextInjectionPipeline.cs` | `ModelFileLocator`, `WhisperModelSession`, `PcmWav` helper, `WhisperTextInjectionPipeline` (lazy model load, transcribe, inject). |
| `PrimeDictate.csproj` | Target `net8.0`; NAudio, SharpHook, Whisper.net + runtimes. |
| `Directory.Build.props` | Shared assembly/file `Version` (installers read this too). |
| `scripts/Publish-Windows.ps1` | Self-contained `win-x64` publish to `artifacts/win-x64/publish`. |
| `scripts/Build-Installers.ps1` | Publishes then builds **WiX** `.wixproj` targets (NuGet `WixToolset.Sdk`) → MSIs in `artifacts/installer`. |
| `installer/wix/` | WiX: offline MSI bundles model + Start Menu shortcut; online MSI uses **WixToolset.Util** `WixQuietExec` (deferred) to run `DownloadModel.cmd`; `Branding.wxs` + `PrimeDictate.ico` for ARP/exe icon. |

## Conventions to preserve

- **Disposal order for Whisper**: `WhisperProcessor` is disposed with `DisposeAsync` first; then `WhisperFactory.Dispose()` (see `WhisperModelSession`).
- **Native / unmanaged**: Prefer `await using` and explicit disposal paths; do not add redundant `try`/`catch` unless there is a clear recovery story.
- **Hotkey handler**: The hook runs on SharpHook’s thread; work is offloaded with `Task.Run` and `await` the dictation path carefully to avoid re-entrancy issues. `DictationController` uses a `SemaphoreSlim` for toggle mutual exclusion.
- **Text injection**: **Do not** reintroduce “set clipboard + simulate paste + immediately restore old clipboard” without solving async paste delivery (delay, flush, or full clipboard snapshot/restore). The vetted baseline is `SimulateTextEntry` (see product README for rationale).
- **Model path**: `ModelFileLocator` is the single place for file discovery and the `PRIME_DICTATE_MODEL` override; extend there instead of scattering path logic.

## Dependencies (NuGet)

- **Whisper.net** and **Whisper.net.Runtime** (required). Optional acceleration: **Whisper.net.Runtime.Cuda.Windows**, **Whisper.net.Runtime.Vulkan** (runtime choice follows Whisper.net’s published priority).
- **NAudio** for capture and resampling.
- **SharpHook** for the global hook and `EventSimulator`.

**TextCopy** is not used; do not add it back unless you implement a clipboard strategy that is demonstrably free of the paste/restore race.

## Extension points (expected evolution)

- **Hotkey**: Change `IsDictationHotkey` in `GlobalHotkeyListener` (`Program.cs`); key codes in `SharpHook.Data.KeyCode`.
- **Whisper options**: `WhisperProcessorBuilder` in `EnsureSessionAsync` (language, threads, `WithSingleSegment`, etc.). Language is currently `WithLanguageDetection()`.
- **Non-Windows audio**: `DefaultMicrophoneRecorder` is Windows-centric (`WasapiCapture`); a cross-platform build would need an abstraction and platform-specific capture.

## Shipped shell + onboarding notes

The tray/onboarding milestone is now implemented:

1. **Host process**: WPF tray host (`App.xaml`) with notification icon, Settings/Exit menu, and Idle/Listening tooltip state.
2. **User settings**: Persisted under `%LocalAppData%\PrimeDictate\settings.json`; loaded at startup and applied to `GlobalHotkeyListener`.
3. **First run**: Missing/incomplete settings show `SettingsWindow` before normal tray-only behavior.
4. **WiX launch option**: Offline and online packages include finish-page launch checkbox to start `PrimeDictate.exe`.
5. **Preserved invariants**: `ModelFileLocator`, Whisper disposal order, hook-thread offload, and `SimulateTextEntry` baseline remain intact.

## Build and test hints

- **Windows installers**: See [installer/README.md](installer/README.md). Offline MSIs require `models/ggml-large-v3-turbo.bin` on the maintainer machine; the blob is still **not** stored in git. Online MSIs need only `dotnet build` + publish output.
- Run from the **repository root** so `./models/ggml-large-v3-turbo.bin` resolves when the working directory is the project folder; otherwise rely on the upward directory walk in `ModelFileLocator`.
- A running `PrimeDictate.exe` from `dotnet run` can **lock the apphost**; stop the process if `MSB3021` / copy-to-output fails.
- Linter: project should build with **0 warnings** under default SDK analysis when possible; platform-specific API use should stay behind `OperatingSystem` checks or documented trade-offs.

## Out of scope (unless explicitly requested)

- Cloud APIs, always-on online STT, or shipping the GGML model inside the repository (it is a large binary; document download instead).
