# Architecture

John's Twitch Handler is a local-first Windows streaming companion. It is a self-hosted ASP.NET Core application whose interface runs in the user's normal browser.

```text
Browser dashboard
        |
        v
ASP.NET Core on http://127.0.0.1:5274
        |
        +-- OBS WebSocket
        +-- Twitch OAuth, Helix, and EventSub
        +-- Spotify OAuth PKCE and Web API
        +-- TikFinity local WebSocket
        +-- Windows, NVIDIA, and network monitoring
        +-- encrypted credentials and local JSON data
```

## Project layout

- `src/StreamSidekick/Program.cs` is the composition root, event coordinator, and Minimal API surface.
- `src/StreamSidekick/Models.cs` contains request, response, snapshot, and report records.
- `src/StreamSidekick/Services` separates external connections, monitoring, storage, and streamer tools.
- `src/StreamSidekick/wwwroot` contains the dependency-free HTML, CSS, JavaScript, overlays, and chat popout.
- `src/StreamSidekick.Installer` embeds the secret-free published payload into one per-user Setup executable.
- `packaging` contains the nontechnical tester launcher and first-run instructions.
- `build-tester-package.ps1` creates a self-contained Windows Setup executable with public application IDs only.

## Runtime model

Most services are singletons because one process represents one local stream workstation. Hosted background services perform bounded monitoring, token renewal, connection recovery, safe scheduled actions, and optional TikFinity chat ingestion. The browser uses non-overlapping recursive refresh timers so a slow request cannot create a backlog.

The Handler does not capture video or encode a stream. OBS remains responsible for rendering and encoding, while the Handler sends control requests and reads status information.

## Storage

Credentials are written to `%LOCALAPPDATA%\StreamSidekick\connections.protected` as one Data Protection payload. The key ring is protected with Windows DPAPI. Human-readable JSON files under the same application directory hold sessions, layouts, commands, automations, raid history, viewer notes, and non-secret preferences.

Browser localStorage is reserved for display and per-browser preferences such as theme, card layout, optional-integration choices, and manual Preflight reminders. Secrets must never be added there.

No SQL or cloud database is used. A database becomes relevant only if approved requirements introduce accounts, synchronization, shared teams, remote administration, or cloud backup.

## External integration boundaries

- OBS uses its authenticated local WebSocket interface.
- Twitch uses a public Client ID, device authorization, renewable user tokens, Helix, and EventSub WebSockets.
- Spotify uses OAuth PKCE and the Web API. A future playback provider interface may allow a reviewed RPC alternative without removing the supported Web API fallback.
- YouTube live chat is an official display-only embed; no Google OAuth token is stored.
- Tangia is controlled through its OBS Browser Source rather than an undocumented account API.
- StreamElements account authorization and stream keys remain inside SE.Live and OBS.
- TikFinity is disabled by default and, when enabled, is read-only through its local desktop WebSocket.

## Distribution

Owner and tester builds come from the same approved source and carry the same visible release number. Configuration differs per installation, and login data never crosses installations. The tester receives one versioned Setup executable; opening a newer release refreshes the installed program without replacing user data.
