# PrimeDictate

A locally hosted, global hotkey dictation utility for fast desktop workflows. It captures the default microphone, runs speech-to-text with [Whisper.net](https://github.com/sandrohanea/whisper.net) (whisper.cpp bindings), and **types the transcript into the focused application in real time**—periodically re-decoding the growing buffer and correcting earlier guesses with backspace plus new text—using SharpHook (no synthetic paste, no clipboard round-trip on the hot path).

## Features

- **Global hotkey**: Configurable global toggle (default `Ctrl+Shift+Space`) to start/stop recording.
- **Tray workspace UI**: Open **Workspace** from the tray icon to browse per-session dictation threads and global runtime logs in a clearer, column-based dashboard layout.
- **Log signal over noise**: Repeated adjacent log entries are collapsed (for example `(... x12)`) and history is capped to keep memory usage predictable.
- **Real-time transcription**: While recording, the app periodically re-transcribes **all audio captured so far** and updates the focused application. You see words appear and refine as you speak, similar to inline completion: the model may revise an early guess as it hears more context.
- **Correction strategy**: Each update compares the new full transcript to the text the app has already injected. It keeps the longest matching prefix, sends **Backspace** for the stale suffix, then types the new suffix (`SimulateKeyStroke` for backspace, `SimulateTextEntry` for characters). Stopping dictation runs one **final** full pass so nothing after the last live tick is lost.
- **Audio**: Windows default capture device via NAudio **WASAPI** (`WasapiCapture`), resampled to **16 kHz, 16-bit, mono PCM** for Whisper.
- **Mic isolation mode (best effort)**: Optional exclusive-capture setting can block other apps from the mic on supported devices; if exclusive capture fails, PrimeDictate automatically falls back to shared mode and continues dictation.
- **Inference**: [Whisper.net](https://www.nuget.org/packages/Whisper.net) `1.9.0` with `Whisper.net.Runtime` plus optional **CUDA** and **Vulkan** runtimes for hardware acceleration when available.
- **Injection**: [SharpHook](https://www.nuget.org/packages/SharpHook) `EventSimulator` for Unicode text entry and backspace (no synthetic paste, no clipboard round-trip on the hot path).

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (or a compatible SDK that can build `net8.0` projects).
- **Windows** is the primary target (WASAPI capture path). Other platforms may require a different capture implementation.
- For CPU Whisper at runtime, the published **Whisper.net** requirements apply (for example, Visual C++ redistributable and instruction-set expectations); see the [Whisper.net readme](https://www.nuget.org/packages/Whisper.net).

## Model file

The app looks for a GGML model named **`ggml-large-v3-turbo.bin`**. Suggested source: the [ggerganov/whisper.cpp](https://huggingface.co/ggerganov/whisper.cpp) collection on Hugging Face (for example, `ggml-large-v3-turbo` in the [model table](https://huggingface.co/ggerganov/whisper.cpp)).

Place the file in one of the following (first match wins):

1. Path in the `PRIME_DICTATE_MODEL` environment variable (full path to the `.bin` file).
2. `./models/ggml-large-v3-turbo.bin` (relative to the process current working directory, when that path exists).
3. `AppContext.BaseDirectory` + `models/ggml-large-v3-turbo.bin` (useful if you copy the model next to the published app).
4. A walk upward from the current directory to find `models/ggml-large-v3-turbo.bin` (helps when `dotnet run` uses a `bin/...` working directory but the repository root contains `models/`).

**Example (PowerShell, from repo root)**, downloading the file named in the upstream `main` file list:

```powershell
New-Item -ItemType Directory -Force -Path "models" | Out-Null
Invoke-WebRequest -Uri "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin" -OutFile "models/ggml-large-v3-turbo.bin"
```

The model is large (on the order of 1.5 GiB). First transcription after launch loads it and may take noticeable time and disk I/O.

## Public Windows release (installers)

This repo targets **64-bit Windows** only. Maintainers build **MSI packages** with the [WiX Toolset](https://wixtoolset.org/) **through NuGet** (`WixToolset.Sdk`). Only the **[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)** is required—no separate Inno Setup or WiX install.

| MSI | When to use |
|-----|-------------|
| **Offline** | Same app install surface as online (**Program Files** payload, Start Menu shortcut, ARP branding, finish-page launch option). Difference: includes `models\ggml-large-v3-turbo.bin` inside the MSI, so no Hugging Face access at install time. Requires the model file locally when **building** (not committed to git). |
| **Online** | Same app install surface as offline (**Program Files** payload, Start Menu shortcut, ARP branding, finish-page launch option). Difference: installs **`DownloadModel.cmd`** / **`RunDownloadModelElevated.cmd`** and runs an elevated **WiX QuietExec** download step; `curl` progress is written to the MSI log (for example `msiexec /i PrimeDictate-….msi /l*v install.log`). Requires network access to Hugging Face during install. |

**Build both** (offline requires `.\models\ggml-large-v3-turbo.bin`):

```powershell
.\scripts\Build-Installers.ps1
```

**Online only** (no local model file):

```powershell
.\scripts\Build-Installers.ps1 -Installer Online
```

Outputs: `artifacts\installer\`. Version comes from `Directory.Build.props`.

See [installer/README.md](installer/README.md) for details. Redistribute the GGML model only in compliance with its license/terms.

### Tray shell and first-run setup

PrimeDictate now runs as a **WPF tray app** (no console window in normal use):

- **Tray shell**: Notification-area icon with **Open Workspace**, **Settings**, and **Exit** menu items.
- **Tray status colors**: **Ready = Blue**, **Recording = Red**, **Error = Yellow**. Tooltip text follows app state (`Ready`, `Listening`, `Error`).
- **First launch**: If `%LocalAppData%\PrimeDictate\settings.json` is missing or incomplete, a setup window appears to capture hotkey and tray click behavior.
- **Configurable hotkey**: Global hotkey is loaded from saved settings and applied to `GlobalHotkeyListener` at startup (default remains `Ctrl+Shift+Space` until changed).
- **Model override in settings**: Setup window now includes a model file picker that sets a process-local `PRIME_DICTATE_MODEL` override.
- **Installer continuity**: Offline and online MSIs share one product identity (same MSI name + upgrade family), so installing either flavor upgrades/replaces the other.
- **Installer finish launch**: Both MSI flavors expose **“Launch PrimeDictate when setup completes”** (checked by default), which starts the app after install.

**Publish folder only** (no installer):

```powershell
.\scripts\Publish-Windows.ps1
```

## Build and run

```powershell
cd path\to\PrimeDictate
dotnet run
```

The app starts in the tray. On first launch, complete setup, then focus another application and use your configured hotkey to dictate. Transcript appears at the caret **while recording** and finalizes when you press the hotkey again.

**Live loop tuning** (in source, `DictationController`): default tick is about **1.5 s** between snapshot attempts; a snapshot needs at least about **0.55 s** of audio, and ticks are skipped when the PCM buffer size has not grown (silence / pause) to avoid redundant model runs.

**Note:** Stopping a running `dotnet run` (or any running `PrimeDictate.exe`) may be required before `dotnet build` can replace `bin\...\PrimeDictate.exe` on Windows (file lock on the apphost).

### Using real-time dictation reliably

- Keep the **caret** in the field where you want text; the app does not move focus for you.
- Avoid **manually editing** the span PrimeDictate is inserting into during a session. Correction assumes the document still matches what the app last typed; if you change it by hand, backspaces can delete the wrong characters.
- **Cost**: Each live tick runs Whisper on the **entire** recording from session start, so work grows with session length. Long monologues are heavier than short phrases; a faster or smaller model helps if this becomes limiting.

## Configuration surface

| Mechanism | Purpose |
|-----------|---------|
| `PRIME_DICTATE_MODEL` | Absolute path to the GGML model file, if not using the default `models/ggml-large-v3-turbo.bin` layout. |
| `WhisperProcessorBuilder` | Language detection and other inference options are set in `WhisperTextInjectionPipeline` (`WithLanguageDetection()`, etc.). |
| Live loop constants | `LiveTranscribeInterval` and `LiveMinAudio` in `DictationController` (`Program.cs`). |
| User settings + first-run | Stored at `%LocalAppData%\PrimeDictate\settings.json` with `FirstRunCompleted`, dictation hotkey, tray click behavior, model override, and optional exclusive mic capture toggle. |

## Architecture (high level)

| Area | Technology |
|------|------------|
| Hotkey | SharpHook `SimpleGlobalHook`, keyboard only; gesture loaded from settings and matched on `KeyPressed`. |
| Capture | NAudio `WasapiCapture` + `MediaFoundationResampler` to 16 kHz mono PCM. |
| Live snapshots | `DefaultMicrophoneRecorder.TryGetPcm16KhzMonoSnapshot` copies and resamples the current buffer while recording (same format as the final `StopAsync` buffer). |
| Live loop | `DictationController.LiveTranscriptionLoopAsync`: background task started with recording; cancelled when recording stops, then a final transcribe-and-sync runs on the full take. |
| Transcription | `WhisperFactory.FromPath` → `WhisperProcessor` → `ProcessAsync` over an in-memory WAV stream built from PCM; full segment text is assembled per pass. |
| Typing / correction | `WhisperTextInjectionPipeline` keeps `committedTargetText`, then `SyncCommittedTextToTarget`: longest common prefix, `SimulateKeyStroke(Backspace)`, `SimulateTextEntry` for the suffix; `BeginLiveDictationSession` resets state when a new session starts. |

### Why not clipboard + Ctrl+V?

An earlier design put the transcript on the clipboard, simulated **Paste**, then restored the previous clipboard. Many applications handle paste **asynchronously**, so the restore often ran **before** the app read the new clipboard, and users saw the **old** clipboard (for example, a recently copied URL). The current design avoids that class of race by not using the clipboard for injection in the first place.

## License

This repository’s application code is provided as in-repo source; follow the licenses of the dependencies (Whisper.net, NAudio, SharpHook, and the GGML model terms from their respective publishers) when redistributing.
