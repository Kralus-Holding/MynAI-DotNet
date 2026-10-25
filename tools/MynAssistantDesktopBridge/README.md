# MynAssistant Desktop Bridge

This Windows bridge follows the current command/claim implementation in
`MynKralus/MynAssistant-Android/desktop-agent/agent.py` (Android source version 0.2.1),
with Windows-installer-specific status reporting and headless startup (packaged bridge
version 0.2.2).
The bridge connects the installed Windows PC to Android and the web admin panel
through the shared Firebase project; it does not expose an inbound PC port.

## Connection flow

1. The installer installs `MynAssistantDesktopBridge.exe` next to `MynAssistant.exe`.
2. By default, the installer enables the per-user Windows startup entry
   `MynAssistant.DesktopBridge` and launches the bridge hidden after install.
   The checkbox can be turned off during installation or later in Settings → Agent.
3. On first launch, a browser opens for Google OAuth. Sign in with the same Google
   account used by MynAssistant Android. The bridge then registers the Windows
   device in Firestore and sends heartbeats every 25 seconds.
4. Android discovers the PC through the Firestore `devices` collection and sends
   approved commands into the `commands` collection. The bridge only claims
   commands for the current user and this device; command claiming uses Firestore
   update-time preconditions to prevent a cancelled or stale command from running.
5. The web control panel uses the Render API and its Firebase service account to
   list the same device documents and queue approved commands. The Render API
   must be deployed and its `FIREBASE_SERVICE_ACCOUNT_JSON` environment variable
   must have Firestore access.

Logs are written to
`%LOCALAPPDATA%\MynAssistant\DesktopAgent\bridge.log`.
The Google Desktop OAuth client is stored at
`%LOCALAPPDATA%\MynAssistant\Google\client_secret.json`. Do not commit that
file or any user session/service-account secrets.

## Supported remote actions

The current bridge supports volume mute/up/down, show desktop, relative mouse move,
left/right click, safe predefined Windows actions, Unicode text entry (up to 1000
characters), Backspace, and Enter. It rejects unsupported commands and checks
user/device ownership before execution.

## Local build and tests

```powershell
py -m pip install -r tools/MynAssistantDesktopBridge/requirements.txt Pillow
py -m unittest discover -s tools/MynAssistantDesktopBridge/tests -v
py -m PyInstaller --noconfirm --clean --onefile --noconsole --name MynAssistantDesktopBridge --collect-all google_auth_oauthlib --collect-all google.auth --hidden-import win32crypt tools/MynAssistantDesktopBridge/agent.py
```

The Windows installer workflow runs the same tests and generates the Windows
application icon, tray icon and Inno Setup logo from
`assets/branding/mynassistant-icon-source.png` before compiling .NET or Inno Setup.
