# Focused regression checks

Run from the repository root. JavaScript checks need Node 20 or newer; the application itself does not need Node.

```powershell
node --test tests/music-overlay.test.cjs
node --test tests/layout-defaults.test.cjs
node --test tests/tester-kit.test.cjs
dotnet run --project tests/MusicOverlayChecks --configuration Release
```

These checks use temporary preferences and sample music only. They do not load saved connections or contact OBS, Twitch, Spotify, or Tangia.

The tester-kit checks validate local document links, guide navigation, privacy reminders, version notes, and the Windows packaging helper. The packaging check copies only reviewed guides into a unique temporary folder and removes that scratch copy afterward; it does not build or install the app. It also confirms that the standalone HTML is identical to the guide served by the app.

For an isolated visual check, run the C# checks with `-- --serve` followed by the absolute path to `src/StreamSidekick/wwwroot`. Open `http://127.0.0.1:5287/music-overlay.html`. The fixture provides `PUT /api/overlay/music` with `{"visible":false}` or `{"visible":true}`, and `PUT /fixture/boss-focus/true` or `/false`. Stop the fixture with Ctrl+C. It never changes the live Handler on port 5274.

Use `-- --serve-layout` instead to check the real dashboard layout with its account/monitoring startup disabled. Open `/index.html` for desktop or `/fixture/narrow` for a 390-pixel-wide embedded viewport. Layout writes stay in fixture memory and in this separate localhost origin's browser storage.
