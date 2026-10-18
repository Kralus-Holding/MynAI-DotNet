# MynAssistant Desktop Bridge bundled with MynAI

This folder contains the Windows Firebase bridge agent used to connect Android MynAssistant to the installed Windows MynAI application.

Canonical source: `MynKralus/MynAssistant-Android/desktop-agent`, branch `feature/android-pc-firebase-bridge`, source blob SHA `d38880884c14b431ddfb2dbc04270cde40bf41b1` at the time of vendoring.

Keep this copy in sync when the canonical Android-repository agent changes. The packaged executable is named `MynAssistantDesktopBridge.exe`.

## OAuth configuration

The agent first looks for `client_secret.json` beside its executable (standalone/developer mode), then falls back to:

`%LOCALAPPDATA%\MynAI\Google\client_secret.json`

The Windows installer is designed to place the desktop OAuth client configuration in this per-user location. Never commit a real OAuth JSON file, refresh token, Firebase service-account key, or user session to source control.

## Local build

```powershell
py -m pip install -r tools/MynAssistantDesktopBridge/requirements.txt
py -m unittest discover -s tools/MynAssistantDesktopBridge/tests -v
pyinstaller --noconfirm --clean --onefile --console --name MynAssistantDesktopBridge --collect-all google_auth_oauthlib --collect-all google.auth --hidden-import win32crypt tools/MynAssistantDesktopBridge/agent.py
```


## Firestore screen-frame size

Screen frames are reduced through several resolution/quality levels until the encoded Base64 string is at most 650 KB. This checks the actual string stored in Firestore, not only the smaller binary JPEG payload, leaving room for the other fields in the document.
