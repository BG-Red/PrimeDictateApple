# Windows installers (WiX / MSI)

Installers are **64-bit Windows Installer packages (.msi)** built with the [WiX Toolset](https://wixtoolset.org/) **through NuGet** (`WixToolset.Sdk`). You only need the **.NET 8 SDK**; you do **not** install WiX separately.

The **online** MSI references **`WixToolset.Util.wixext`** (elevated model download). See that package’s [license / terms](https://github.com/wixtoolset/wix/blob/main/OSMFEULA.txt) if you redistribute installers.

| MSI | Contents |
|-----|----------|
| **Online** (`PrimeDictate-*-Windows-Online.msi`) | App under `Program Files\PrimeDictate`, **Start Menu** shortcut, and **Add/Remove Programs** icon. After files are installed, a **deferred QuietExec** custom action (LocalSystem) runs **`DownloadModel.cmd`** so **`curl`** can write under Program Files. Console output (including **`curl --progress-bar`**) is captured in the MSI log, not in a separate window. **`RunDownloadModelElevated.cmd`** is included if you prefer a visible UAC/console flow. |

## Installer UX

- **Online MSI**: Uses WiX UI with **“Launch PrimeDictate when setup completes”** (checked by default). If checked, setup launches `[INSTALLFOLDER]PrimeDictate.exe` from the finish dialog after the deferred model download custom action runs in execute sequence.
- **First-run app entry**: Launching at install finish lands users in the app’s first-run setup when `%LocalAppData%\PrimeDictate\settings.json` is not yet completed.
- **Branding continuity**: ARP metadata, MSI names, Start Menu shortcut text, and finish-page launch prompt now align with the app’s branded status language (**Ready=Blue, Recording=Red, Error=Yellow**).
- **Upgrade continuity**: The online MSI keeps the existing product identity (`Name` + `UpgradeCode`) for clean upgrades.
- **Language**: The installer is pinned to `en-US` UI resources for consistent English setup dialogs.

## Prerequisites (maintainer)

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Build

From the repository root:

```powershell
.\scripts\Build-Installers.ps1
```

Outputs are copied to `artifacts\installer\`. Intermediate build outputs live under `installer\wix\online\bin\`.

## Layout

| Path | Role |
|------|------|
| `wix/shared/AppPayload.wxs` | `Program Files\PrimeDictate` tree and harvested publish payload |
| `wix/shared/Branding.wxs` | ARP icon + common Add/Remove Programs metadata |
| `wix/shared/StartMenuShortcuts.wxs` | Shared Start Menu shortcut component used by the online installer |
| `wix/online/` | Online package, **Util** QuietExec download, helper `.cmd` scripts |
| `wix/assets/PrimeDictate.ico` | App + installer icon (also **`ApplicationIcon`** on `PrimeDictate.exe`) |
| `wix/assets/DownloadModel.cmd` | Curl download used by QuietExec and by the elevated helper |
| `wix/assets/RunDownloadModelElevated.cmd` | Optional manual re-download with visible UAC |

## Version

`Package` / MSI product version uses `Directory.Build.props` (`Version`) with a fourth field `.0` for Windows Installer (for example `1.0.0` → `1.0.0.0`).

## End-user notes

- Install is **per machine** (`Scope="perMachine"`) under **Program Files**.
- **Visual C++ Redistributable** may be required for Whisper native code; see [Whisper.net](https://www.nuget.org/packages/Whisper.net).
- Downloaded models remain subject to their publishers’ terms; redistribute only in compliance with those terms.
