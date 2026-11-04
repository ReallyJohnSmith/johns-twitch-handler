# Release .016 checks

Prepared November 3, 2026 for a small Windows tester group.

## Files

- Tester installer: `artifacts/tester-packages/Johns-Twitch-Handler-Tester-v016-Setup.exe`
- Size: 81,694,881 bytes (about 78 MiB).
- SHA-256: `8938CF999F21522B327B667D7AC58535743116B61A72724041B2C1DD6F0AB97F`
- Owner build: `artifacts/release-v016/owner/`
- Verified tester payload: `artifacts/release-v016/tester-verified/`
- Source file hashes: `artifacts/release-v016/source-manifest.json`

The installer includes the offline tester guide. Share the Setup executable,
not the owner folder, extracted verification files, source ZIP, or development
website helper. The existing public Twitch application ID is retained from the
previous tester package; no account tokens or passwords are copied.

## Passed

- Stable .NET SDK 9.0.320 Release build: zero warnings and errors.
- Self-contained Windows x64 package with .NET and ASP.NET runtime 9.0.22.
- All 37 JavaScript checks and syntax checks for every dashboard JavaScript file.
- Six music-overlay persistence checks and 19 website-pairing/isolation checks.
- Product-branding scan across current source and the complete extracted payload.
- Version .016 in package settings and tester guides.
- Loopback-only default address; no experimental website origin or sign-in startup.
- Spotify, Tangia, StreamElements, and TikFinity disabled by default for testers.
- Payload inspected for personal data, credential files, logs, and debug files.
- Installer verification mode extracts successfully into a fresh directory.
- Verification-mode update from .014 to .016 refreshes the version and retains
  an existing sample file. The last locally available numbered installer was .014.
- Follow-up verification-mode update from the published .012 version to .016
  also retains an existing sample file, and the resulting executable, settings,
  launcher, and dashboard match the clean .016 extraction.
- All packaged PowerShell scripts parse successfully.
- Owner/tester executable and selected dashboard asset hashes match.
- Offline guide matches the application guide byte for byte.
- Isolated browser fixture: startup failure and successful retry, Settings,
  narrow dashboard layout, and .016 tester guide navigation/content.
- Git whitespace checks.

## Remaining hands-on checks

The extraction tests do not exercise shortcuts, actual installed user data,
Windows security prompts, or a clean Windows account. Complete a fresh install
and a normal upgrade on a separate Windows test PC, then verify saved connections
and reports. Test OBS and Twitch authorization there before a live stream.
No live accounts, stream controls, or the owner's installed app were changed.

This installer is unsigned. The guide explains how testers can ask John for help
if Windows warns. No installer was uploaded, no invitation was sent, and no Git
commit or release tag was created. Existing local work remains in the checkout.
If distributing through GitHub, upload this exact installer and verify access
with an invited tester before sharing its link. Direct private sharing is also
possible using the prepared invitation in `docs/tester-kit/INVITATION.md`.

## GitHub handoff check — November 3, 2026

Authenticated GitHub API checks confirm that the repository is private. Its only
published release is the .012 prerelease (`v0.0.12`), containing the v012 Setup
executable. The latest successful GitHub build covers commit `995cf30`; it does
not cover the local .016 changes. Unauthenticated repository access returns 404.

Before directing testers to GitHub:

1. Commit and push the reviewed .016 source and tester guides, then require the
   build for that commit to pass.
2. Create the .016 prerelease (`v0.0.16`) from that commit and attach the verified
   v016 Setup executable and SHA-256 file. Prepared release text is in
   `docs/RELEASE-NOTES-016.md`.
3. Grant the intended testers access to the private repository and have one
   tester confirm that the release, installer, and guide are accessible.
4. Complete the hands-on installation/account checks above before live use.

Repository visibility and access have not been changed by this check. There is
no verified GitHub download link for .016 yet.
