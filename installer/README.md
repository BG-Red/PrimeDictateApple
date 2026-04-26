# Windows installers (WiX / MSI)

Installers are **64-bit Windows Installer packages (.msi)** built with the [WiX Toolset](https://wixtoolset.org/) **through NuGet** (`WixToolset.Sdk`). You only need the **.NET 8 SDK**; you do **not** install WiX separately.

The **online** MSI also references **`WixToolset.Util.wixext`** (elevated model download). See that package’s [license / terms](https://github.com/wixtoolset/wix/blob/main/OSMFEULA.txt) if you redistribute installers.

| MSI | Contents |
|-----|----------|
| **Offline** (`PrimeDictate-*-Windows-Offline.msi`) | Self-contained app under `Program Files\PrimeDictate`, **`models\ggml-large-v3-turbo.bin`**, **Start Menu** shortcut, and **Add/Remove Programs** icon. Requires the model file on the **maintainer** machine when building (not in git). |
| **Online** (`PrimeDictate-*-Windows-Online.msi`) | Same app layout and scripts. After files are installed, a **deferred QuietExec** custom action (LocalSystem) runs **`DownloadModel.cmd`** so **`curl`** can write under Program Files. Console output (including **`curl --progress-bar`**) is captured in the MSI log, not in a separate window. **`RunDownloadModelElevated.cmd`** is included if you prefer a visible UAC/console flow. |

## Installer UX

- **Offline MSI**: Uses WiX UI with **“Launch PrimeDictate when setup completes”** (checked by default). If checked, setup launches `[INSTALLFOLDER]PrimeDictate.exe` from the finish dialog.
- **Online MSI**: Mirrors the same finish-page launch behavior after install (and after the deferred model download custom action runs in execute sequence).
- **First-run app entry**: Launching at install finish lands users in the app’s first-run setup when `%LocalAppData%\PrimeDictate\settings.json` is not yet completed.
- **Branding continuity**: ARP metadata, MSI names, Start Menu shortcut text, and finish-page launch prompt now align with the app’s branded status language (**Ready=Blue, Recording=Red, Error=Yellow**).
- **Upgrade continuity**: Online and offline MSIs share the same product identity (`Name` + `UpgradeCode`), so installing either flavor upgrades/replaces the other instead of creating parallel entries.
- **Language**: Both installer projects are pinned to `en-US` UI resources for consistent English setup dialogs.

## Prerequisites (maintainer)

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- For **offline** MSIs: `models\ggml-large-v3-turbo.bin` at the repo root (see main [README](../README.md))

## Build

From the repository root:

```powershell
.\scripts\Build-Installers.ps1
```

**Online only** (no local model file):

```powershell
.\scripts\Build-Installers.ps1 -Installer Online
```

Outputs are copied to `artifacts\installer\`. Intermediate build outputs also live under `installer\wix\offline\bin\` and `installer\wix\online\bin\`.

## Layout

| Path | Role |
|------|------|
| `wix/shared/AppPayload.wxs` | `Program Files\PrimeDictate` tree and harvested publish payload |
| `wix/shared/Branding.wxs` | ARP icon + common Add/Remove Programs metadata |
| `wix/shared/StartMenuShortcuts.wxs` | Shared Start Menu shortcut component (used by both installers) |
| `wix/offline/` | Offline package and embedded model fragment |
| `wix/online/` | Online package, **Util** QuietExec download, helper `.cmd` scripts |
| `wix/assets/PrimeDictate.ico` | App + installer icon (also **`ApplicationIcon`** on `PrimeDictate.exe`) |
| `wix/assets/DownloadModel.cmd` | Curl download used by QuietExec and by the elevated helper |
| `wix/assets/RunDownloadModelElevated.cmd` | Optional manual re-download with visible UAC |

## Version

`Package` / MSI product version uses `Directory.Build.props` (`Version`) with a fourth field `.0` for Windows Installer (for example `1.0.0` → `1.0.0.0`).

## End-user notes

- Install is **per machine** (`Scope="perMachine"`) under **Program Files**.
- **Visual C++ Redistributable** may be required for Whisper native code; see [Whisper.net](https://www.nuget.org/packages/Whisper.net).
- The GGML model is subject to its publishers’ terms; redistribute only in compliance with those terms.
