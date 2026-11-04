# John's Twitch Handler

John's Twitch Handler is a Windows app I built to make running a stream easier.

This folder is the independent **Twitch Helper** project. Open `Twitch Helper.code-workspace`
in your editor. The existing `StreamSidekick.sln`
is the Visual Studio entry point. See `AGENTS.md` for the working scope and `CONTRIBUTING.md`
for build instructions. The personal calendar lives separately in `../our-days-calendar/`.

Instead of jumping between OBS, Twitch, Spotify, performance tools, and notes, the Handler keeps the things I use most in one dashboard. It helps me get ready before I go live, keep an eye on the stream while I am playing, and review the important moments afterward.

Everything runs locally on the streaming computer. Connections, reports, viewer notes, and preferences are not synced to a separate service.

The current approved release is **.016**. The `main` branch may contain newer work that has not been packaged into a numbered release yet.

## One-minute tour

[Watch or download the video](https://github.com/ReallyJohnSmith/johns-twitch-handler/raw/refs/heads/main/docs/media/Johns-Twitch-Handler-60-Second-Tour.mp4) · [Download captions](https://github.com/ReallyJohnSmith/johns-twitch-handler/raw/refs/heads/main/docs/media/Johns-Twitch-Handler-60-Second-Tour.srt)

A quick look at getting ready, keeping stream controls nearby, saving highlights, and reviewing a session. The video is an MP4, about 6.5 MB, with captions on screen. It shows the features included in .016.

## Trying it out?

New here? Start with the short [README for testers](README-FOR-TESTERS.md).
Website hosting is still an exploratory idea. John can review the
[website concept README](website/README.md); tester instructions currently focus on the Windows app.
The [product review](docs/PRODUCT-REVIEW.md) covers the latest usability and efficiency improvements, verification, and remaining priorities.

Start with the [tester guide](docs/tester-kit/README.md). It walks through downloading the app, getting connected, and a short first test. You do not need to write code or install developer tools.

If something is confusing or does not work, just tell me what happened. A plain message is fine; you do not need a GitHub account to send feedback privately.

## How it helps

### Before the stream

The pre-stream page gives me one place to check OBS, Twitch, my microphone, internet connection, and the optional tools I plan to use. I can also add personal reminders for anything that is easy to forget before going live.

The Handler checks CPU, GPU, memory, and network headroom so I have a better idea of whether the computer is ready before I start the stream.

### During the stream

The live dashboard keeps the most useful controls close by:

- Change OBS scenes and monitor audio.
- Mute or unmute OBS sources.
- Save the Replay Buffer.
- Mark highlights and create Twitch clips.
- Follow chat, alerts, raids, rewards, and ad breaks.
- Manage Spotify song requests.
- Watch stream health without digging through several OBS windows.

Release .016 adds separate RPG and FPS profiles, including kill and death markers and keyboard shortcuts for FPS sessions. World of Warcraft addon tools are currently paused and hidden by default.

### After the stream

When the stream ends, the Handler saves a local session report. It includes the stream length, highlights, scene changes, chat activity, performance warnings, and other events that may be useful later.

Previous sessions can be opened from the dashboard. Highlight markers and clip links can also be exported to CSV or passed to StreamLadder for editing and publishing.

## Make the dashboard your own

The Handler includes Simple and Advanced modes.

Simple mode focuses on the current status and the next action I need to take. Advanced mode shows the full dashboard. If I only need one Advanced tool, I can add that panel to Simple mode without enabling everything else.

Panels can be moved, resized, collapsed, floated, docked, or stacked as tabs. The layout is saved locally. The **Reset layout** option restores the recommended arrangement without removing connections, themes, or other preferences.

Release .016 includes three visual styles:

- **Broadcast Console** is compact and practical.
- **Vibe Dashboard** uses rounder cards and more spacing.
- **Tactical Console** uses squared controls, darker surfaces, and amber accents.

New installations start with **Broadcast Console**, the **Ocean** theme, and the **RPG** game profile. Existing installations keep the choices that were already saved.

## Privacy

The Handler runs on the same Windows computer as OBS. Session reports, layouts, viewer notes, raid history, and preferences remain on that computer.

OBS, Twitch, and Spotify credentials are encrypted for the current Windows account. They are not included in tester packages.

Session reports are stored here:

```text
%LOCALAPPDATA%\StreamSidekick\sessions
```

The older `StreamSidekick` folder name is kept so updates do not lose existing reports or connections.

## Getting started

### Tester setup

See the [step-by-step tester guide](docs/tester-kit/README.md) if this is your first visit. On GitHub, download the file ending in **Setup.exe** from [Releases](https://github.com/ReallyJohnSmith/johns-twitch-handler/releases), not the source-code ZIP. If no Setup file is listed, ask me for the installer.

1. Run the supplied `Setup.exe` while you are not streaming.
2. Open **John's Twitch Handler** from the Desktop or Start Menu.
3. Complete the first-run setup.
4. Connect OBS. Approve Twitch access only if you want to try the Twitch features.
5. Bookmark <http://127.0.0.1:5274/> if you want to open the dashboard directly.

Running a newer installer updates the Handler without removing saved connections, reports, or preferences.

### Running from source

The project requires Windows and the .NET 9 SDK.

```powershell
git clone https://github.com/ReallyJohnSmith/johns-twitch-handler.git
cd johns-twitch-handler
dotnet restore .\StreamSidekick.sln --configfile .\NuGet.Config
.\start-streamsidekick.ps1
```

You can also start the server directly:

```powershell
dotnet run --project .\src\StreamSidekick\StreamSidekick.csproj
```

Then open <http://127.0.0.1:5274/>.

## Connecting OBS

1. In OBS, open **Tools → WebSocket Server Settings**.
2. Enable the WebSocket server and keep authentication enabled.
3. Leave the address as `ws://127.0.0.1:4455` unless the OBS port was changed.
4. Enter the OBS WebSocket password in the Handler settings.
5. Enable the OBS Replay Buffer if highlight markers should save replays.

The Handler continues watching OBS even when the dashboard tab is closed. Starting a stream creates or resumes a session. Stopping the stream finishes the report.

## Connecting Twitch

The dashboard shows a clean, read-only Twitch message list without the Stream Chat header, gift leaderboard, or promotional panels. Use **Open Twitch chat** for replies, emotes, and Twitch's moderation controls. Connect Twitch in Settings to receive messages in the Handler.

Tester builds use Twitch's device authorization flow. Select **Connect with Twitch**, approve the connection in the browser, and the Handler will reconnect automatically the next time it starts.

Twitch access is used for the features enabled in the dashboard, including chat, alerts, raids, clips, song-request rewards, and ad timing. No Twitch Client Secret is stored or distributed.

## Optional integrations

Spotify, Tangia, StreamElements, and TikFinity can each be turned on or off for a computer. Turning one off hides its panel and stops its background work without affecting the other integrations.

- **Spotify** adds song search, queue controls, Channel Point requests, and a music overlay. Playback control requires Spotify Premium.
- **Tangia** controls a matching OBS Browser Source and can be muted automatically during supported WoW encounters.
- **StreamElements SE.Live** remains responsible for multistream accounts and stream keys. The Handler helps confirm readiness and estimate upload demand.
- **TikFinity** can provide a read-only local TikTok chat feed while the TikFinity app is running.
- **WARDOGS Tracker** can compare community-server kills and deaths against a saved pre-stream baseline.

## OBS overlays

Add this URL to an OBS Browser Source for a messages-only chat view:

```text
http://127.0.0.1:5274/chat-overlay.html
```

Use this URL for the current Spotify track and request queue:

```text
http://127.0.0.1:5274/music-overlay.html
```

A 600×420 Browser Source is a good starting size for the music overlay. Add `?limit=5` to show up to five upcoming tracks. The overlay can be hidden from the Spotify panel without stopping music or song requests.

## World of Warcraft tools

These tools are currently paused and hidden by default. They are not part of the first tester checklist. The notes below describe the existing implementation for builds where they are enabled.

The Handler can scan Retail and Classic addons, show SavedVariables growth, and safely enable or disable addons for a character while WoW is closed.

It also includes a small profiler addon that uses Blizzard's supported addon profiling tools. To collect a snapshot:

1. Close WoW and select **Install profiler addon** in the Handler.
2. Enable **John's Twitch Handler Profiler** in WoW.
3. Play the content you want to measure.
4. Enter `/ssp`, followed by `/reload`.
5. Return to the Handler and select **Rescan**.

For automatic encounter highlights, turn on combat logging with `/combatlog`. Successful boss encounters can create a stream marker and save the OBS Replay Buffer while a Handler session is active.

## Keyboard shortcuts

- `Alt+M` saves a highlight marker.
- `Alt+R` saves the OBS Replay Buffer.
- `Alt+Shift+K` marks an FPS kill.
- `Alt+Shift+D` marks an FPS death.
- `Alt+Arrow` moves the focused dashboard panel.

These shortcuts work while the dashboard tab is focused.

## Building and testing

Run the checks from the repository root:

```powershell
dotnet restore .\StreamSidekick.sln --configfile .\NuGet.Config
dotnet build .\StreamSidekick.sln --configuration Release --no-restore
node --check .\src\StreamSidekick\wwwroot\app.js
node --test .\tests\music-overlay.test.cjs
node --test .\tests\chat-dashboard.test.cjs
node --test .\tests\layout-defaults.test.cjs
node --test .\tests\tester-kit.test.cjs
dotnet run --project .\tests\MusicOverlayChecks --configuration Release --no-restore
```

Node 20 or newer is needed for the JavaScript checks. The Handler itself does not require Node.

To create the self-contained Windows tester installer:

```powershell
.\build-tester-package.ps1 -TwitchClientId "YOUR_PUBLIC_TWITCH_CLIENT_ID"
```

The installer is written to `artifacts\tester-packages`. Saved credentials, reports, viewer notes, raid history, automations, and logs are not included.

Future installers also include the tester kit. Testers can open **Tester guide** in Settings, or open **Tester kit → START HERE.html** in the installed folder. The guide works without a connection; GitHub links and account sign-in still need internet access.

## More information

- [Tester guide](docs/tester-kit/README.md)
- [First test checklist](docs/tester-kit/CHECKLIST.md)
- [Send feedback](docs/tester-kit/FEEDBACK.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Contributing](CONTRIBUTING.md)
- [Security policy](SECURITY.md)
- [Release workflow](RELEASE_WORKFLOW.md)
- [Release history](CHANGELOG.md)
