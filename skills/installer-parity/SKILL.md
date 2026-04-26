# Installer Parity (Online vs Offline)

Use this skill whenever you change WiX/MSI setup in `installer/wix`.

## Rule

`PrimeDictate.Online` and `PrimeDictate.Offline` installers must stay identical in user experience and branding.

The only intentional product difference is model delivery:

- **Offline** embeds `ggml-large-v3-turbo.bin` in the MSI payload.
- **Online** downloads the model after file install (`DownloadModel.cmd` / `WixQuietExec`).

Both installers must also share a single MSI product identity for upgrade compatibility:

- Same `Package Name` (ARP display name should be `PrimeDictate`).
- Same `UpgradeCode` so online/offline upgrades replace each other.
- No side-by-side product identity split between online and offline flavors.

## Must-Match Surface Area

- Package/product naming style and branding language.
- WiX UI flow and finish-page behavior (including launch checkbox text and defaults).
- Start Menu shortcut presence, naming, icon, and description.
- ARP branding metadata (icon/comments/contact and related product presentation).
- Installed app payload under `Program Files\PrimeDictate` (excluding model embedding/downloader scripts).

## Implementation Guidance

1. Put shared MSI UX/branding/components in `installer/wix/shared`.
2. Reference shared fragments from both `PrimeDictate.Online.wixproj` and `PrimeDictate.Offline.wixproj`.
3. Avoid online-only/offline-only forks unless they are strictly model-delivery related.
4. If you must diverge, document why in `installer/README.md` under Installer UX.

## Validation Checklist

- Build both installers:
  - `.\scripts\Build-Installers.ps1 -Installer Online`
  - `.\scripts\Build-Installers.ps1 -Installer Offline`
- Manually verify both installers:
  - English WiX UI text.
  - Same dialogs/options and finish behavior.
  - Same Start Menu entry and icon.
  - Same ARP entry branding.
  - App launches with branded tray states (Ready blue, Recording red, Error yellow).
