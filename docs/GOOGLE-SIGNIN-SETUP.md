# Windows Google Sign-In Provisioning

## Purpose

MynAssistant account sign-in and optional Google Contacts are separate features. Windows Google Sign-In requests only `openid`, `email`, and `profile`; it must not request Contacts, Gmail, or Drive access.

The desktop sign-in client configuration is app/operator-provisioned. End users are not asked to choose an OAuth JSON file, and no OAuth client JSON or user credential belongs in Git.

## Application configuration and installer packaging

The installer bundles the shared Desktop OAuth client configuration, when configured, under:

`{app}\Auth\google-signin-desktop.json`

The app reads the file beside its installed executable. For compatibility, it falls back to the older machine-wide path `%ProgramData%\MynAssistant\Auth\google-signin-desktop.json` if that file exists.

### Configure it once for releases

1. In the private GitHub repository, open **Settings → Secrets and variables → Actions → New repository secret**.
2. Set the name to `MYNASSISTANT_GOOGLE_DESKTOP_OAUTH_JSON_B64`.
3. On the build computer, base64-encode the downloaded Google Desktop OAuth JSON and save the one-line result as that secret. Do not commit the JSON to Git.
4. The Windows Actions workflow validates that the value contains an `installed` object with `client_id`, `client_secret`, `auth_uri` and `token_uri`, then adds it to the installer payload. The JSON is not printed in build logs.
5. If the secret is missing, CI may still produce a test artifact, but the workflow will not publish a public preview EXE release.

For example, in PowerShell on the computer where the JSON was downloaded:

```powershell
$path = "$env:USERPROFILE\Downloads\client_secret_....apps.googleusercontent.com.json"
[Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) | Set-Clipboard
```

Replace the example file name with the actual downloaded filename. Paste the copied one-line value into the GitHub Actions secret, not into source code or a public message.

Google Desktop OAuth apps are public clients: anything shipped in the installer can be extracted, so the desktop `client_secret` must not be treated as a confidential server credential. The packaged file must contain only application client configuration—never a user's access token, refresh token, ID token or Firebase session. Google OAuth tokens obtained by the sign-in-only flow are held in memory; the resulting Firebase session is stored separately through the existing Windows DPAPI-protected session store.

## Google Cloud and Firebase prerequisites

1. In the Google Cloud project owned for MynAssistant, create/configure an OAuth client for the supported Windows desktop authorization flow. Keep it separate from Google Contacts API scopes and from personal/developer credentials.
2. Configure the OAuth audience/user type and publishing status intentionally. An external OAuth app left in Testing is limited to the configured test-user allowlist; Google documents a maximum of 100 test users and that test-user authorization expires after seven days. For general availability, move to the appropriate production status and complete any verification Google requires for the chosen scopes/branding.
3. In Firebase Console, enable Google under Authentication sign-in providers and confirm that the configured provider/client settings are compatible with the Google ID token audience used by this Windows flow. A mismatch can fail at Firebase's `accounts:signInWithIdp` exchange even when the browser login itself succeeds.
4. Validate the full flow with a non-developer test account and a separate new-account test before announcing availability.

## Current limitations

- The repository does not contain a provisioned Google OAuth client JSON, and this change does not modify Google Cloud Console or Firebase Console settings.
- Until the approved configuration is provisioned and the Firebase ID-token exchange has been tested in the real project, Google sign-in is **not verified end-to-end**. If configuration is missing or Google returns `access_denied`, MynAssistant should show an actionable error rather than claiming success.
- Direct Google sign-in does not grant Google Contacts access. Contacts remains an independent, optional integration.

## Official references

- [Google OAuth 2.0 for desktop applications](https://developers.google.com/identity/protocols/oauth2/native-app)
- [Firebase Authentication: Google sign-in](https://firebase.google.com/docs/auth/web/google-signin)
- [Google Cloud: manage app audience and publishing status](https://support.google.com/cloud/answer/15549945)
