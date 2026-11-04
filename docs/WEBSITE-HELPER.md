# Website dashboard and Windows helper

Development implementation; not an approved numbered installer release.

The hosted dashboard is built from the same HTML, CSS, and JavaScript as the local
dashboard. Its API calls go directly to `http://127.0.0.1:5274` on the visitor's PC.
No cloud relay or multi-user database is involved. OBS connections, OAuth tokens,
reports, background internet checks, and computer monitoring stay in the helper.
The website's scripts can access the paired helper's APIs, so deployment access
to the website is a security boundary. Only trusted code should be published there.

## Pairing

1. Install a website-enabled helper package. It starts at Windows sign-in.
2. Open the website, then open its **helper connection page** link.
3. The local page displays the exact approved website origin. Approve it to get
   a code, then enter that code on the website within five minutes.
4. This browser remembers its own pairing in IndexedDB. Account credentials
   remain encrypted in the helper. Clearing website data requires pairing again.
5. Remove a browser from the local connection page, or use **Forget this browser**
   on the website while the helper is running. Revocation takes effect immediately.

Codes are single-use, expire in five minutes, and lock after five failed attempts.
Browser capabilities are 256-bit random values. Only hashes are retained by the
helper, inside a Windows-account-protected file. Capabilities remain valid until
revoked; there are at most 20 per installation. Code/token values never appear in
URLs. The helper checks the exact configured HTTPS origin, capability, loopback
remote address, and local Host header. Local management endpoints cannot be used
by the hosted dashboard, even when paired. Changing domains requires changing the
helper's WebsiteOrigin and pairing again; old-origin capabilities stop working.

The browser still needs to allow local/loopback access. Chrome and Edge are the
initial compatibility targets. On a return visit, browsers that expose a granted
loopback permission reconnect automatically; otherwise **Check connection** supplies
the required user gesture. A website cannot silently launch native software.
**Start installed helper** uses the registered `johnshandler:` protocol and may
show a browser prompt. The protocol accepts no command-line arguments from URLs.

## Build and release

Build the hosted assets into a separate directory:

```powershell
.\build-website.ps1 -DestinationDirectory ..\Johns-Twitch-Handler-Web\dist
```

The builder uses an explicit public-asset allowlist. OBS overlay URLs still point
to the local helper; they never contain browser pairing credentials. Downloads
use authenticated requests, then browser blob downloads.

After feature/release approval, package a new numbered release using the existing
release workflow and the additional option:

```powershell
.\build-tester-package.ps1 -TwitchClientId 'PUBLIC_CLIENT_ID' -WebsiteOrigin 'https://YOUR-WEBSITE'
```

For development review before release approval, add `-DevelopmentPreview` and omit
`-ReleaseVersion`. This writes an explicitly unnumbered installer under
`artifacts/development-packages/`, marks its runtime version `website-preview`, and
leaves `RELEASE_VERSION.txt` and approved tester packages unchanged. This is a
development artifact, not a tester release. Installing it normally still replaces
the installed Handler and enables the website helper; extraction verification uses
`--verify-install <scratch-directory>` and does not install, create shortcuts,
register a protocol, stop the Handler, or launch any account connections.

That option enables Windows sign-in startup and website opening. It does not
install a Windows service or change firewall rules. Normal packages retain the
local launcher. The new `website-pairings.protected` file lives alongside existing
connections and reports, outside the installed application folder.

Before a tester release: verify a fresh install, upgrade, Windows sign-in startup,
custom protocol launch, pair/restart/revoke, and actual OBS/Twitch/Spotify features.
Add the verified new installer download to the website. The development website
explicitly says the updated installer is not released, rather than linking an
older incompatible installer. Follow RELEASE_WORKFLOW.md for version approval.

## Checks

```powershell
dotnet restore tests/WebsiteBridgeChecks/WebsiteBridgeChecks.csproj --configfile NuGet.Config
dotnet run --project tests/WebsiteBridgeChecks --configuration Release --no-restore
node --check src/StreamSidekick/wwwroot/website-connect.js
node --check src/StreamSidekick/wwwroot/pair.js
node --test tests/website-connection.test.cjs
```

The bridge checks use temporary storage and no real integrations. The optional
`--browser-fixture` argument runs a pairing-only helper on port 5274 for browser
testing when no real helper is listening. It deliberately does not serve the
dashboard APIs beyond health and a test action. Never use it for a real stream.

## Development verification, October 29, 2026

- Solution builds without warnings or errors.
- 19 real HTTP bridge/security checks pass using isolated temporary storage.
- Six browser-transport tests pass, including persisted pairing, origin isolation,
  disconnect recovery, and revoke-before-forget behavior.
- 18 existing dashboard/overlay/layout/tester-guide checks pass.
- The private hosted connection screen was inspected in the in-app browser.
- The in-app browser could not complete the HTTPS-to-loopback request. A direct
  HTTP request with the expected website Origin succeeded against the same fixture.
  End-to-end pairing in Chrome/Edge is **not yet verified**.
- Windows sign-in startup, protocol registration, clean install, upgrade, and live
  OBS/Twitch/Spotify behavior require installer/live-environment verification.
- No numbered installer was released, no existing installation was replaced, and
  no real streaming connections were used during these tests.
- The pending reconnect fix was published to the same private Site on resumption.
- A 78 MB development installer was built and its embedded payload was extracted
  with `--verify-install`. Website origin/startup metadata, required executable,
  pairing assets, launcher scripts, offline guide, and absence of private runtime
  files were checked. Actual Windows startup/upgrade behavior remains unverified.
- Development artifact: `artifacts/development-packages/Johns-Twitch-Handler-Website-Preview-Setup.exe`.
  SHA-256: `4766EE702F1245911768628E1FB38047B0FE7508A9C24C3341B6AD15AE0E9C13`.
- This preview contains the public Twitch application ID from the existing v014
  package. It contains no Spotify application ID; an existing saved Spotify
  connection or a user-supplied application ID is still needed for Spotify setup.
