# John's Twitch Handler

John's Twitch Handler is a local Windows companion for Twitch streams. It combines an OBS control dashboard, highlight/clip workflow, post-stream reports, a preflight reminder for NVIDIA Broadcast, a low-overhead CPU/GPU monitor, and a viewer-facing music queue overlay.

Sharing the project with a regular ChatGPT conversation? Upload
[`CHATGPT_README.md`](CHATGPT_README.md) for a safe, standalone product and architecture briefing.

The owner and tester builds use the same approved feature set. Each installation keeps its own connection data locally; credentials are never packaged or synchronized. See `RELEASE_WORKFLOW.md` for the release rules.

The current approved release is **.008**. `RELEASE_VERSION.txt` controls tester package numbering;
the owner and tester badges show the same release number, while the tester ZIP also includes it in
the filename.

Bookmark `http://127.0.0.1:5274/` to open the installed Handler. The URL stays the same between releases and dashboard files are revalidated after every restart, so the bookmark shows the currently installed approved build.

Developer references: [architecture](docs/ARCHITECTURE.md), [contributing rules](CONTRIBUTING.md), [security policy](SECURITY.md), [release workflow](RELEASE_WORKFLOW.md), and [GitHub connection checklist](docs/GITHUB_SETUP.md).

## Current MVP

- Connects directly to OBS WebSocket 5.x with challenge-response authentication.
- Shows and changes the active OBS scene.
- Provides a compact OBS audio mixer with live meters, mute controls, and volume sliders.
- Saves the OBS Replay Buffer and creates timestamped highlight notes.
- Optionally creates Twitch clips and retains their edit links for StreamLadder.
- Embeds the channel's Twitch chat directly in the dashboard.
- Receives follows, subscriptions, gift subs, resubs, cheers, raids, channel-point rewards, and live/offline alerts through Twitch EventSub.
- Connects to Spotify with OAuth PKCE, searches tracks, controls the active Spotify queue, and turns a named Twitch Channel Point reward into moderated or automatic song requests.
- Lets every dashboard card be rearranged by dragging its header or using four-way Alt+Arrow shortcuts, then remembers the layout.
- Lets unused cards collapse to a compact header with the `−` button and remembers which cards are collapsed in that browser; `+` expands them again.
- Inventories Retail and Classic WoW addons, shows SavedVariables growth, and safely enables/disables addons per character while WoW is closed.
- Bundles a lightweight in-game profiler companion that exports Blizzard's addon execution metrics after `/ssp` and `/reload`.
- Tracks session duration, markers, scene changes, CPU, GPU, VRAM, RAM, and GPU temperature.
- Combines system load with OBS render misses, encoder skips, and output congestion in a live headroom guard with actionable WoW-focused warnings.
- Checks internet availability and latency every five seconds, timestamps outages, slow responses, instability, and recovery, and adds notable events to active session reports.
- Watches WoW combat logs for boss pulls and victories, then automatically creates session markers and optionally saves the OBS Replay Buffer.
- Saves reusable per-edition addon presets and safely applies them to any discovered character profile while WoW is closed.
- Diagnoses likely CPU, GPU, or VRAM pressure in the post-stream report.
- Detects NVIDIA Broadcast and opens it from the preflight panel.
- Reads only non-secret OBS video/encoder settings and compares them with a performance-first preset.
- Exports highlight timestamps and Twitch edit URLs as CSV.

All session data stays on the computer under `%LOCALAPPDATA%\StreamSidekick\sessions`. This legacy folder name is intentionally retained so the rename does not lose existing reports or connections. Successful OBS, Twitch, and Spotify connections are encrypted for the current Windows account with Windows DPAPI and reused for automatic reconnects.

## Run it

From PowerShell:

```powershell
cd C:\Users\reall\source\repos\StreamSidekick
.\start-streamsidekick.ps1
```

Or run the server directly:

```powershell
dotnet run --project .\src\StreamSidekick\StreamSidekick.csproj
```

Then open <http://127.0.0.1:5274>.

## OBS connection

1. In OBS, open **Tools → WebSocket Server Settings**.
2. Enable the WebSocket server and keep authentication enabled.
3. Leave the default address as `ws://127.0.0.1:4455` unless the OBS port was changed.
4. Enter the OBS WebSocket password in John's Twitch Handler Settings and connect.
5. Enable OBS Replay Buffer if the **Save OBS replay** marker option will be used.

## Performance-first Twitch preset

The initial recommendation is deliberately conservative:

| Setting | Value |
|---|---|
| Base canvas | 1920×1080 |
| Scaled output | 1280×720 |
| Frame rate | 30 FPS |
| Encoder | NVIDIA NVENC H.264 |
| Rate control | CBR |
| Video bitrate | 3500 Kbps |
| Audio bitrate | 160 Kbps |
| Keyframe interval | 2 seconds |
| NVENC preset | P4 |
| Multipass mode | Single pass |
| Look-ahead | Off |
| Psycho Visual Tuning | On |
| B-frames | 2 |
| Downscale filter | Bicubic |

This keeps the canvas compatible with existing scenes while lowering output rendering and encoding demand. The reviewed bitrate is 3500 Kbps because the previous 6000 Kbps stream showed no congestion; the additional 500 Kbps improves WoW UI readability without adding meaningful CPU load. If later monitoring shows reliable CPU headroom and motion clarity matters more than game responsiveness, 720p60 at about 4500 Kbps is the next step up.

The optimizer can apply this preset while OBS is closed. Before changing anything, it saves the active profile's original video and encoder configuration under `%LOCALAPPDATA%\StreamSidekick\optimizer-backup`. **Restore previous settings** puts that captured configuration back, including the original advanced encoder file. John's Twitch Handler never changes these settings silently, and both actions refuse to run while OBS is open.

## OBS volume control

After connecting OBS, the **Volume control** card lists every audio-capable OBS input. Its meters update live; **Mute** and **Unmute** act immediately, and each slider changes that source's OBS volume from silence through +6 dB. The mixer is another movable dashboard card and does not change Windows system volume.

## Twitch clips and StreamLadder

Twitch clip creation and live alerts are optional. Owner builds place the Handler's public Twitch Client ID in `appsettings.json`; ordinary testers only click **Connect with Twitch**. Twitch's device authorization supplies the channel name, Broadcaster ID, access token, and renewable refresh token automatically. No Client Secret is stored or distributed. Advanced manual fields remain available for development and recovery only.

Use these token scopes for the complete feature set:

- `clips:edit`
- `moderator:read:followers`
- `channel:read:subscriptions`
- `bits:read`
- `channel:read:redemptions`
- `user:read:chat`
- `user:write:chat`
- `channel:read:ads`
- `channel:edit:commercial`

The redemption and chat scopes let John's Twitch Handler receive song requests, detect new viewer messages for the private OBS Monitor Only chime, and post results back to chat. The two ad scopes detect automatic/manual ad starts and optionally start a commercial from the dashboard. Reconnect Twitch once after scope additions so the renewable token contains the complete set.

The Twitch access and refresh tokens are stored as a Windows-account-encrypted payload. John's Twitch Handler rotates the refresh token before the short-lived access token expires, saves the replacement atomically, and reconnects EventSub automatically. A public-client refresh token can require authorization again after 30 days of inactivity, or sooner if the Twitch password changes or the application is revoked. After a clip is created, use its **Edit clip** link and then **Open StreamLadder**, or export the report CSV to organize several highlights.

## Spotify song requests

Spotify playback control requires a Spotify Premium account and a developer application:

1. Create an application at the Spotify developer dashboard.
2. Register `http://127.0.0.1:5274/api/spotify/callback` as a redirect URI.
3. Copy the application's Client ID into John's Twitch Handler Settings and click **Connect Spotify**.
4. Start playback in the Spotify desktop app so Spotify Connect has an active device.
5. Create a Twitch custom Channel Point reward that requires viewer text. Its title must match the title in the **Spotify requests** card; the default is `Song Request`.
6. Keep **Auto-queue matches** off to approve requests manually, or enable it to send permitted matches directly to Spotify.

The integration requests only `user-read-playback-state` and `user-modify-playback-state`. Access and refresh tokens are encrypted for the current Windows account and are cleared when Spotify is explicitly disconnected. The non-secret Client ID is also remembered by the browser.

Spotify, Tangia, StreamElements, and TikFinity are optional per computer. Each has an explicit **Yes / No** choice in Settings. Choosing No hides its card, stops its integration work, and treats its preflight row as intentionally skipped. It does not remove the feature or delete another service's account. TikFinity defaults to No in both owner and tester builds.

## Build a nontechnical tester package

The package script creates a self-contained Windows build, inserts only public application IDs, defaults Spotify, Tangia, StreamElements, and TikFinity to skipped unless explicitly enabled, and produces a ZIP under `artifacts\tester-packages`. It never includes saved credentials, reports, viewer notes, raid history, automations, or local logs.

```powershell
.\build-tester-package.ps1 -TwitchClientId "YOUR_PUBLIC_TWITCH_CLIENT_ID"
```

If a tester should also start with Spotify or Tangia enabled:

```powershell
.\build-tester-package.ps1 -TwitchClientId "YOUR_PUBLIC_TWITCH_CLIENT_ID" -SpotifyClientId "YOUR_PUBLIC_SPOTIFY_CLIENT_ID" -EnableSpotify -EnableTangia -EnableStreamElements -EnableTikFinity
```

The tester extracts the ZIP and double-clicks **Open John's Twitch Handler.cmd**. The first-run setup opens automatically. They paste their OBS WebSocket password and approve Twitch; no .NET installation, database, developer dashboard, Client ID, Broadcaster ID, or manually generated token is required. A secret-free diagnostic export is available in Settings for support.

John's Twitch Handler controls the active Spotify client; it does not capture audio or change OBS routing. Keeping Spotify off the Twitch VOD track does not itself grant broadcast rights, so verify that your music use complies with the applicable Spotify and Twitch terms.

### Microphone preflight

Preflight identifies `Mic/Aux`, an explicitly named microphone, or an OBS audio-input capture. A muted microphone keeps readiness yellow and can be unmuted directly from Preflight. If the source cannot be identified, the user is directed to the OBS mixer instead of the app guessing at a desktop-audio source.

### Manual preflight reminders and visibility

Preflight starts with a manual **Get Coffee** reminder and accepts up to 12 custom reminders. Their definitions and checkbox state stay in that browser only, and they make the readiness border yellow until checked. Completion resets after a tracked session ends so the next show starts honestly. Each manual reminder can be removed with its × button.

**Open preflight list** controls only the visibility of built-in rows. Hiding a row does not disconnect its service, change an optional integration's Yes / No setting, or remove that requirement from readiness. **Show all fields** restores the full display.

### StreamLadder clip handoff

**Create clip for TikTok / YouTube** creates the Twitch clip immediately and adds its public URL to the post-stream StreamLadder publishing queue. **Copy clip + open StreamLadder** places that URL on the clipboard and opens StreamLadder's supported browser workflow. TikTok and YouTube completion checkboxes are local reminders only. Editing, social authentication, scheduling, and publishing remain inside StreamLadder because no supported public publishing API is assumed.

### StreamElements and multistream

John's Twitch Handler uses the StreamElements SE.Live OBS plugin as the multistream engine instead of running another encoder inside the Handler. Install SE.Live, open OBS, and connect each destination from **StreamElements > SE.Live Docks > StreamElements Multi-Streaming**. Account authorization and stream keys remain inside StreamElements and OBS; the Handler never reads or stores them.

The **StreamElements & multistream** card detects the plugin, remembers which destinations this computer intends to use, and estimates combined upload demand. After checking the selected destinations in the SE.Live dock, mark **Outputs confirmed in OBS** so preflight can turn green without pretending the Handler can read private account state. The upload target is 1.5 to 2 times the sum of the selected output bitrates so there is room for connection variation. TikTok requires LIVE access, and a 1080 by 1920 SE.Live vertical canvas is recommended for its output. StreamElements is optional per computer and is skipped by default in tester packages unless `-EnableStreamElements` is supplied.

### TikFinity TikTok chat

TikFinity is an optional third-party bridge for displaying TikTok chat beside Twitch and YouTube in the dashboard. Install and open the TikFinity desktop app, then choose **Use TikFinity for TikTok chat? Yes** in Handler Settings. The Handler connects read-only to TikFinity's local Event API at `ws://127.0.0.1:21213/`; it does not log into TikTok or store TikTok credentials. Choosing No closes that local connection, clears the in-memory TikTok messages, and stops the browser status poll. TikFinity defaults to No for every new owner and tester installation.

### OBS music queue overlay

Add `http://127.0.0.1:5274/music-overlay.html` to OBS as a Browser Source. A 600×420 source is a good starting size. The overlay shows the current track and the next three queued tracks, including viewer names when the track came from a channel-point request. Add `?limit=5` to the URL to show up to five upcoming tracks.

Successful song additions update this queue and receive a Twitch chat confirmation. They are deliberately excluded from the alert path, so John's Twitch Handler does not play a separate “song added” sound.

## Tangia controls

Add Tangia's overlay to OBS as a Browser Source and name it **Tangia** (a `tangia.co` URL is also detected automatically). The Tangia card can show/hide, mute, change volume, reload the source, and toggle **Boss Focus**. WoW encounter monitoring can automatically mute Tangia at `ENCOUNTER_START` and restore its previous mute state at `ENCOUNTER_END`.

Tangia does not currently expose a documented public event feed used by this app. **Log interaction** therefore records a manual timestamp and optional note in the session report and CSV without scraping or depending on an undocumented service.

## Ad break timer

The Ad break card can start a Twitch commercial for eligible live Affiliate/Partner channels, or run a timer-only break. Twitch EventSub also catches automatic ad starts. Start and completion messages are posted to Twitch chat, while both boundaries are timestamped in the session report. Timer-only mode announces and records the break but does not ask Twitch to serve an ad.

## Game Chat popout

Paste the current YouTube livestream URL in Settings to place YouTube's official live-chat embed beside Twitch chat. The video ID is only saved as a browser preference. This display-only integration requests no Google login and cannot send messages as the YouTube channel. Posting through the YouTube Live Chat API would require a separate Google OAuth application and YouTube account permission.

**Game overlay** in the Chat card opens the configured Twitch and YouTube chats in Document Picture-in-Picture when supported by Chrome/Edge, which keeps them above a borderless-windowed game. A compact browser window is used as fallback. Exclusive fullscreen games can cover ordinary desktop overlays, so run WoW in borderless-windowed mode for reliable visibility.

## Safe actions and schedules

The **Action & schedule manager** supports only three reviewed actions: send a Twitch chat message, change the current OBS scene, or add a local stream marker. An action may be run manually or repeated every 5 to 240 minutes while a Handler session is active. It cannot execute scripts, shell commands, programs, downloads, or arbitrary URLs. Scheduled chat messages are intentionally labeled Twitch-only until a separate YouTube OAuth integration is added.

## Raid history

Every incoming Twitch raid and every outgoing raid marked in the Handler is appended to `%LOCALAPPDATA%\StreamSidekick\raid-history.json`. The Raid Manager provides one-week, two-week, and one-month timeline views. Removing a channel from the suggested shortlist does not erase its historical events. Up to 5,000 events are retained locally.

## World of Warcraft addon health

The **Addon health** card scans each installed WoW edition and separates three different signals:

- Folder size: useful for finding oversized databases, but not proof of runtime load.
- SavedVariables size: large persistent data can increase login, logout, and reload time.
- Profiler time: recent average, session average, peak execution time, and counts of 10ms/50ms spikes from Blizzard's `C_AddOnProfiler` API.

Select a character profile to enable or disable individual addons. WoW must be closed; every change creates an `AddOns.txt.streamsidekick.bak` backup beside the character's `AddOns.txt` file.

To collect runtime data:

1. Close WoW and click **Install profiler addon** in John's Twitch Handler.
2. Enable **John's Twitch Handler Profiler** in WoW's AddOns screen.
3. Play representative content such as a raid, dungeon, or busy city.
4. Enter `/ssp`, then `/reload` to write the latest snapshot to disk.
5. Click **Rescan** in John's Twitch Handler. The list switches from disk size to measured recent/peak CPU time.

### Addon presets

Choose a character, enter a preset name such as **Raid Stream** or **Mythic+ Stream**, and click **Save current**. Applying a preset updates the selected character and first creates `AddOns.txt.streamsidekick.bak`. World of Warcraft must be closed when a preset is applied.

### Automatic encounter highlights

The **Encounter highlights** card watches WoW's live combat log without reading game memory or injecting into the client. To enable the log, enter `/combatlog` in WoW. Version 0.2 of the bundled profiler also provides `/ssp events` as a convenient toggle.

With a John's Twitch Handler session active, successful boss encounters create a `wow-boss` marker. If **Save OBS replay** is enabled and OBS is connected with Replay Buffer active, the replay is saved at the same time. Use **Test event** before a stream to verify the workflow.

## Keyboard shortcuts

- `Alt+M`: save a highlight marker.
- `Alt+R`: save the OBS Replay Buffer.
- `Alt+Left` / `Alt+Right`: move the focused dashboard card across its current row.
- `Alt+Up` / `Alt+Down`: move the focused dashboard card between rows.

These shortcuts work while the dashboard browser tab is focused. Global hotkeys are planned for the desktop-packaged version.

## Development checks

```powershell
dotnet restore .\StreamSidekick.sln --configfile .\NuGet.Config
dotnet build .\StreamSidekick.sln --no-restore
```
