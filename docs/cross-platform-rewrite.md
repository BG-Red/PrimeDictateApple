# Cross-Platform Rewrite Plan

PrimeDictate can share its dictation workflow, settings schema, history storage, model catalogs, and transcription engines across Windows, macOS, and Linux. The parts that must become platform adapters are UI shell, global hotkeys, audio capture, foreground target tracking, text injection, launch-at-login, update/install, and shell command execution.

## Target Shape

Use a shared .NET core plus thin platform implementations:

| Layer | Responsibility | Cross-platform status |
| --- | --- | --- |
| `PrimeDictate.Core` | Dictation controller, PCM buffer, model catalogs, transcription engines, voice commands, settings/history/stats | Mostly portable after removing direct Windows service construction. |
| `PrimeDictate.App` | Cross-platform UI, tray/status, settings, history, overlay | Replace WPF/Windows Forms with a cross-platform desktop UI. |
| `PrimeDictate.Platform.Windows` | WASAPI capture, Win32 target guard, Unicode `SendInput`, startup shortcuts/registry, MSI/update behavior | Current implementation can move here. |
| `PrimeDictate.Platform.Mac` | CoreAudio/AVFoundation capture, accessibility-based text injection, login item, app bundle updates | New implementation. Requires permissions UX for Accessibility and Microphone. |
| `PrimeDictate.Platform.Linux` | PipeWire/PulseAudio/ALSA capture, X11/Wayland hotkeys/injection strategy, autostart desktop file | New implementation. Wayland support will be compositor-dependent. |

## Recommended UI Rewrite

Move from WPF to Avalonia or another native-feeling cross-platform .NET UI. Avalonia is the most direct fit because the app already uses XAML patterns and MVVM-shaped view models. Rebuild these surfaces first:

- Tray/status shell with Ready, Listening, Processing, Error states.
- Settings window.
- History/workspace window.
- Non-activating transcript overlay.
- First-run onboarding.

Do not try to keep WPF and make it universal. WPF is Windows-only, and `net8.0-windows` plus `<UseWPF>` is the current hard stop.

## Platform Interfaces

The initial code boundary is now:

- `IAudioRecorder`
- `ITextInjector`
- `IForegroundInputTarget`
- `IForegroundInputTargetProvider`

Next interfaces to add:

- `IGlobalHotkeyService`
- `ITrayShell`
- `ILaunchAtLoginService`
- `IClipboardService`
- `IFileDialogService`
- `IUpdateService`
- `IVoiceShellCommandRunner`
- `IPlatformPermissionService`

Keep platform adapters out of the dictation workflow. The controller should consume interfaces only.

## Platform Work

### Windows

- Move current `DefaultMicrophoneRecorder`, `WindowsInputHelpers`, `LaunchAtLoginManager`, Windows update installer flow, and MSI scripts into Windows-specific projects or folders.
- Keep the existing final-only text injection behavior. It is the strongest baseline for Windows editors.

### macOS

- Capture audio through CoreAudio/AVFoundation bindings or a cross-platform audio library that uses CoreAudio under the hood.
- Text injection requires Accessibility permission. Use either native interop for `CGEvent`/AX APIs or a dedicated native helper.
- Foreground target tracking should use AppKit/CoreGraphics process and window APIs where possible.
- Launch at login should use login item APIs or a LaunchAgent.
- Package as `.app` plus `.dmg` or `.pkg`.

### Linux

- Audio should prefer PipeWire/PulseAudio, with ALSA as a fallback only if needed.
- X11 can support global hotkeys and synthetic input through XTest-style APIs.
- Wayland has intentional restrictions around global hotkeys and synthetic text injection. Expect compositor-specific paths, portals, or a documented degraded mode.
- Launch at login can use `~/.config/autostart/*.desktop`.
- Package as AppImage, deb/rpm, or Flatpak depending on distribution target.

## Migration Order

1. Extract the portable dictation core into a `net8.0` class library.
2. Move Windows-only code behind platform interfaces while keeping the existing WPF app working.
3. Add a cross-platform UI shell and bind it to the portable core.
4. Add Windows adapter parity tests against current behavior.
5. Implement macOS audio, hotkey, injection, permissions, and app packaging.
6. Implement Linux audio and X11 support; define Wayland limitations explicitly.
7. Build CI for `win-x64`, `win-arm64`, `osx-x64`, `osx-arm64`, `linux-x64`, and later `linux-arm64`.
8. Retire WPF once the cross-platform UI reaches feature parity.

## Non-Negotiable Behavior To Preserve

- Final-only target injection; no live retyping into editors.
- Foreground target guard before committing text.
- Overlay-only live preview.
- Model catalog validation owns model filenames and folder layout.
- Lazy model/runtime loading.
- No clipboard paste/restore strategy unless async paste delivery and restore timing are proven robust.

## First Risks

- macOS and Linux text injection are permission- and compositor-sensitive. Prototype these before polishing UI.
- `MediaFoundationResampler` is Windows-only; audio conversion must move to a portable resampler or into per-platform capture adapters.
- Current QNN backend is Windows ARM64-specific and should remain unavailable elsewhere.
- Installer/update logic must be split by OS instead of being abstracted too early.
