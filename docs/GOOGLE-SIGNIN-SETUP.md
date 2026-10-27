# Windows Google Sign-In Provisioning

## Purpose

MynAssistant account sign-in and optional Google Contacts are separate features. Windows Google Sign-In requests only `openid`, `email`, and `profile`; it must not request Contacts, Gmail, or Drive access.

The desktop sign-in client configuration is app/operator-provisioned. End users are not asked to choose an OAuth JSON file, and no OAuth client JSON or user credential belongs in Git.

## Application configuration location

The Windows sign-in service checks this machine-wide path:

`%ProgramData%\MynAssistant\Auth\google-signin-desktop.json`

The JSON is expected to be the OAuth client configuration for an installed/desktop app and contain an `installed` object with `client_id`, `client_secret`, `auth_uri`, and `token_uri`. Provision it only through the application's release/deployment process or a machine administrator. Do not copy a developer's personal OAuth file into the repository or ship one without the project owner's approval.

Google OAuth tokens obtained by the sign-in-only flow are held in memory; the flow does not save Google access/refresh tokens to the user's Google Contacts token directory. The resulting Firebase session is saved through the existing Windows DPAPI-protected session store.

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
