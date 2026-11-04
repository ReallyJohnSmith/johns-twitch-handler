# Focused regression checks

Run from the repository root. JavaScript checks need Node 20 or newer; the application itself does not need Node.

```powershell
node --test tests/music-overlay.test.cjs
node --test tests/chat-dashboard.test.cjs
node --test tests/layout-defaults.test.cjs
node --test tests/tester-kit.test.cjs
dotnet run --project tests/MusicOverlayChecks --configuration Release
dotnet run --project tests/WebsiteBridgeChecks --configuration Release
node --test tests/website-connection.test.cjs
node --test tests/dashboard-runtime.test.cjs
```

These checks use temporary preferences and sample music only. They do not load saved connections or contact OBS, Twitch, Spotify, or Tangia.

The tester-kit checks validate local document links, guide navigation, privacy reminders, version notes, and the Windows packaging helper. The packaging check copies only reviewed guides into a unique temporary folder and removes that scratch copy afterward; it does not build or install the app. It also confirms that the standalone HTML is identical to the guide served by the app.

For the clean chat panel, run `node tests/chat-preview.cjs` and open `http://127.0.0.1:5288`. This uses the real markup, styles, and renderer with sample messages only; it does not read accounts or connect to OBS/Twitch. Stop the preview with Ctrl+C. The chat regression checks cover safe text rendering, bounded history, scrollback, connection feedback, and non-overlapping, visibility-aware polling.

For an isolated visual check, run the C# checks with `-- --serve` followed by the absolute path to `src/StreamSidekick/wwwroot`. Open `http://127.0.0.1:5287/music-overlay.html`. The fixture provides `PUT /api/overlay/music` with `{"visible":false}` or `{"visible":true}`, and `PUT /fixture/boss-focus/true` or `/false`. Stop the fixture with Ctrl+C. It never changes the live Handler on port 5274.

Use `-- --serve-layout` instead to check the real dashboard layout with its account/monitoring startup disabled. Open `/index.html` for desktop or `/fixture/narrow` for a 390-pixel-wide embedded viewport. Layout writes stay in fixture memory and in this separate localhost origin's browser storage.

For startup-recovery and Settings review, run `node tests/dashboard-preview.cjs` and
open `http://127.0.0.1:5286`. Startup initially returns a fixture-only error. Send
`POST /fixture/allow-startup`, then select **Try again** in the dashboard. This
serves the real UI with mocked, disconnected services; it reads no installed data
and creates no OBS/account connections. `/fixture/narrow` embeds the UI at 390px.
Stop with Ctrl+C. Runtime checks cover shared reads, timeout/cancellation, hidden
tab suspension, no overlapping polls, startup retry, and health-data freshness.
