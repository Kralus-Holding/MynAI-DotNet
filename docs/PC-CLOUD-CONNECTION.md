# MynAssistant PC Cloud Connection

## The two Windows processes are different

- `MynAgent.exe` is the local .NET Agent. It serves the WPF app's named-pipe interface and scheduled-task features. Its `Agent: bağlı` status does **not** prove that the PC is visible to the phone or web panel.
- `MynAssistantDesktopBridge.exe` is the cloud/remote-control Agent. It signs in with Google OAuth, registers a Windows device document in the existing Firebase/Firestore project with `connectionMode=firebase-agent`, sends a heartbeat, polls the `commands` collection, claims pending commands, executes the allow-listed commands, and updates each command's result.
- The Render-hosted `MynAssistant-Server` is an API and web administration panel, not a persistent TCP relay to the Windows machine. The website's admin routes read/write the same Firestore `devices` and `commands` collections using the configured Firebase service account. Android and the Windows cloud bridge currently access Firestore directly for the live device-command path.

## First-time setup on a different Windows PC

1. Install the self-contained x64 installer. Python and the .NET runtime do not need to be installed separately; the Desktop Bridge is packaged as `MynAssistantDesktopBridge.exe`.
2. Start MynAssistant. In the header, `Bulut PC: kapalı` is the cloud bridge status; click it to start the bridge. Alternatively, open Settings → Agent → start the Phone-PC connection.
3. On first use, complete the Google OAuth browser sign-in using the same Google identity that maps to the Firebase account used by the Android app. The bridge stores its refresh session with Windows DPAPI under the current Windows user.
4. Wait for the bridge log to report `Cihaz çevrimiçi`. The log is `%LOCALAPPDATA%\MynAssistant\DesktopAgent\bridge.log`. The device should appear in Android's device list with `connectionMode=firebase-agent` and a recent `lastSeenAt`.
5. For the web administration panel, Render must be running and its Firebase service-account environment variables must be valid. The admin panel's overview/data-status should confirm Firestore access before the device or command lists can be trusted.

## Local LAN bridge versus cloud bridge

Android also contains a legacy/local PC bridge setting that records a `windows-<uid>` document with `connectionMode=mynai-local-bridge` and a `bridgeUrl`. This is a different route and is intended for a local HTTP endpoint on the same network. It is not the same as the Firestore cloud Agent described above. The .NET desktop app's cloud Agent does not expose an HTTP listener; the cloud Firestore route is the supported remote-command route for this product path.

## Troubleshooting

- If the header says `Bulut PC: kapalı`, click the badge or start it in Settings → Agent. If the executable or OAuth JSON is missing, repair/reinstall the app and verify `%LOCALAPPDATA%\MynAssistant\Google\client_secret.json`.
- If it says `Bulut PC: çalışıyor` but the PC does not appear in Android, check the bridge log for Google sign-in errors, token refresh errors, or Firestore HTTP permission errors. A running process by itself does not prove cloud registration succeeded.
- The Android app sends Firestore command documents with `ownerId`, `deviceId`, `command`, `status=pending`, and a server timestamp. The Windows bridge only executes supported commands for its own device ID and owner. Do not treat a command as successful until the document changes to `completed` (or `failed`) and supplies a result.
- The web admin panel requires its own admin login and a valid Render Firebase service account. It manages Firestore documents; it cannot start a Windows process that is not already running.
- Session files are protected using DPAPI and are tied to the Windows user account that created them. Do not copy another user's session files to a different account or PC.

## Build requirements for the shared logo

Run `python tools/prepare-branding.py` before the .NET build and Inno Setup compile. It creates the transparent logo PNG, replaces the legacy `myn.ico` in the temporary build tree with a multi-size icon from the shared brand source, and creates the 55×58 installer wizard BMP. Do not generate the wizard bitmap by calling `System.Drawing.Icon.ToBitmap()`; that was the source of the previous installer compile failure on the test PC.
