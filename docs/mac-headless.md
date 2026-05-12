# PrimeDictate Headless on macOS

This is the quickest macOS path while the full cross-platform tray UI is being rebuilt.

## Build From Windows

```powershell
$env:DOTNET_CLI_HOME="$PWD\.dotnet-home"
dotnet publish PrimeDictate.Headless\PrimeDictate.Headless.csproj -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o artifacts\macos\osx-arm64
dotnet publish PrimeDictate.Headless\PrimeDictate.Headless.csproj -c Release -r osx-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o artifacts\macos\osx-x64
```

Use `artifacts/macos/osx-arm64` for Apple Silicon Macs and `artifacts/macos/osx-x64` for Intel Macs. Copy the whole folder, not only the executable, because the native `.dylib` files must sit next to `PrimeDictate.Headless`.

## First Run On The Mac

Install the audio capture dependency:

```bash
brew install ffmpeg
```

From the copied artifact folder:

```bash
chmod +x PrimeDictate.Headless
xattr -dr com.apple.quarantine .
./PrimeDictate.Headless --download-model tiny.en
./PrimeDictate.Headless
```

Grant macOS permissions when prompted:

- Microphone
- Accessibility
- Input Monitoring

If macOS does not prompt automatically, add Terminal or `PrimeDictate.Headless` manually in System Settings.

## Usage

Hotkeys:

- `Ctrl+Shift+Space`: start recording; press again to stop, transcribe, and inject text.
- `Ctrl+Shift+Enter`: discard the active recording.
- `Ctrl+C`: exit the terminal runner.

Useful commands:

```bash
./PrimeDictate.Headless --list-audio-devices
./PrimeDictate.Headless --audio-device ":1"
./PrimeDictate.Headless --once 5 --print-only
./PrimeDictate.Headless --once 5 --inject
```

The default macOS ffmpeg audio device is `:0`. If that is not your microphone, run `--list-audio-devices` and pass the audio device as `:<index>`.

Models are stored under `~/Library/Application Support/PrimeDictate/models`.
