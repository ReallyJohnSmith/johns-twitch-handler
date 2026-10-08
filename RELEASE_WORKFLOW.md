# Release workflow

John's Twitch Handler has an owner build and a tester build. Both use the same approved functionality so a test result represents what John will use.

John is the primary user and primary tester. Features must remain useful in his real streaming workflow, not merely look convincing in a demonstration. Demo Mode is an isolated test harness that can be moved or collapsed like any other dashboard card.

Connections are intentionally installation-specific. John's Twitch, OBS, Spotify, and Tangia credentials remain on John's computer. A tester authorizes their own accounts, and those credentials remain encrypted on the tester's computer. Credentials, reports, logs, and viewer data are never copied into a release installer or synchronized between installations.

For each completed change:

1. Verify the shared feature in development.
2. Ask John to approve the feature for release.
3. Build the owner version and tester version separately from the same approved source.
4. Check each artifact before it replaces the previous version.

Each GitHub Release carries one versioned Windows Setup executable. Opening a newer Setup file refreshes the local program files and shortcuts without replacing locally encrypted connections, reports, or preferences.

## Release numbers

`RELEASE_VERSION.txt` is the single tester-package release number and uses the visible `.001`
format. An approved feature set keeps the same number in owner and tester builds. Increment it once
for the next approved release, then rebuild both channels. Tester Setup filenames include `v001`,
`v002`, and so on, while the dashboard badge and health endpoint display `.001`, `.002`, and so on.

## Before inviting testers

The build workflow checks code; it does not upload a downloadable installer. A source push alone is not a tester release.

- [ ] Get John's approval for the feature set and version. Do not reuse an old release number for a changed installer.
- [ ] Build and verify the owner and tester copies from the same approved source.
- [ ] Check the tester installer on a clean 64-bit Windows test environment and check an update over the previous release. Confirm saved settings and reports survive.
- [ ] Confirm the installer includes `Tester kit/START HERE.html` and that Settings opens `/tester-guide.html`. Test the standalone guide with internet disconnected; external links are expected to need a connection.
- [ ] Check the kit's version notes against the release being shipped. Move only approved entries out of Unreleased and update the guide when features become available.
- [ ] Confirm no personal credentials, reports, viewer notes, logs, or owner settings are included. Keep diagnostic exports out of public issue reports.
- [ ] Attach the versioned Setup executable to the intended GitHub Release. Download and check that exact asset before sharing the link. Confirm an invited tester can access it; do not assume repository visibility or access.
- [ ] Add the finished video link only after the approved video is uploaded and playable. The kit does not depend on a video being ready.
- [ ] Send the [tester invitation](docs/tester-kit/INVITATION.md), naming the version and giving testers a private reply route.

The guide source is `src/StreamSidekick/wwwroot/tester-guide.html`. The packaging helper copies the same file as `START HERE.html`, plus the four supporting Markdown guides. To make a standalone kit without building or changing the app:

```powershell
.\packaging\copy-tester-kit.ps1 -DestinationDirectory .\artifacts\tester-kit
Compress-Archive -Path .\artifacts\tester-kit\* -DestinationPath .\artifacts\Johns-Twitch-Handler-Tester-Kit.zip -Force
```

The standalone ZIP contains guides, not the application installer. Start with a fresh output directory for each share; do not add personal notes or diagnostics to it.
