# Contributing to John's Twitch Handler

John is the primary user and product owner. Keep changes practical for a real stream night, local-first, and lightweight enough to run beside a game and OBS.

## Local build

Requirements for source development:

- Windows
- .NET 9 SDK
- Node.js only for the JavaScript syntax check

```powershell
dotnet restore StreamSidekick.sln
dotnet build StreamSidekick.sln --configuration Release --no-restore
node --check src\StreamSidekick\wwwroot\app.js
```

The distributed tester package is self-contained and does not require these developer tools.

## Design boundaries

- Keep the server bound to loopback unless a separately reviewed authentication and network-security design is approved.
- Do not add cloud storage, telemetry, analytics, crash uploads, or remote administration by implication.
- Do not store credentials in browser localStorage, source files, logs, reports, or diagnostic exports.
- Prefer supported APIs and local read-only integrations over scraping or account-session extraction.
- Optional integrations must be visibly optional and should stop their work when disabled.
- Avoid adding another video capture or encoder path; OBS remains responsible for rendering and encoding.
- Preserve the owner/tester feature parity defined in `RELEASE_WORKFLOW.md`.

## Repository safety

Never commit:

- `%LOCALAPPDATA%\StreamSidekick` contents
- access or refresh tokens
- OBS WebSocket passwords
- OAuth client secrets
- exported diagnostics or session reports
- `logs`, `artifacts`, `bin`, `obj`, or tester ZIP files
- signing certificates or private keys

Public OAuth Client IDs are identifiers rather than secrets, but changes to packaged IDs still require owner review.

## Pull requests

Keep one product behavior per pull request when practical. Explain the user-facing result, performance impact, privacy impact, and how it was verified. Do not increment the visible release version until John approves the completed behavior for release.
