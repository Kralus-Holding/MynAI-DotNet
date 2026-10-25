# MynAI-DotNet

Windows desktop implementation of MynAssistant, including the WPF UI, local .NET Agent, and the Firebase/Firestore Desktop Bridge used by Android and the web admin panel.

## Build the Windows installer

After this branch is merged to `main`, run the official local builder from PowerShell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ".\tools\Build-MynAssistant-Installer.ps1"
```

The builder downloads the current merged source into a separate timestamped folder, checks Python 3.12/3.13, .NET 10, and Inno Setup 6, and does not overwrite previous installer outputs. It reuses `%LOCALAPPDATA%\MynAssistant\Google\client_secret.json` or asks you to select the Google Desktop OAuth client JSON. The finished installer is copied to Downloads with a timestamp and SHA-256 hash.

## Shared logo and Windows UI

The source of truth is `assets/branding/mynassistant-icon-source.png`. Before compiling, the build runs:

```powershell
py -3.13 tools/prepare-branding.py
```

The tool emits `src/MynAI.Windows/Resources/mynassistant-logo.png` with transparency, regenerates the multi-size `myn.ico`, and writes the 55×58 Inno Setup wizard BMP. This avoids the previously failing `System.Drawing.Icon.ToBitmap()` conversion. The header places the title and status chip in separate layout columns, and `Beklemede` is displayed without a stray equals sign.

The header has separate indicators:
- **Agent** — local .NET Agent / named-pipe scheduled-task channel.
- **Bulut PC** — the cloud Desktop Bridge process used by the Android app and web admin panel. Click this chip to start the bridge; first use requires Google sign-in.

## Phone / web / PC connection

See [docs/PC-CLOUD-CONNECTION.md](docs/PC-CLOUD-CONNECTION.md) for the current architecture and troubleshooting. The phone and Windows cloud bridge exchange commands through Firebase Authentication and Firestore. The Render-hosted server is the web administration/API layer over the same Firestore collections; it does not start a Windows process by itself.

## Important environment requirements

- Windows x64
- .NET 10 SDK for development builds; the distributed installer publishes the .NET components self-contained.
- Python 3.12 or 3.13 for building the Desktop Bridge EXE; end users do not need Python installed.
- Inno Setup 6 for compiling the installer.
- A Google Desktop OAuth `client_secret.json` configured for the Firebase project used by the Android app.
