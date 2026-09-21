# Release workflow

John's Twitch Handler has an owner build and a tester build. Both use the same approved functionality so a test result represents what John will use.

John is the primary user and primary tester. Features must remain useful in his real streaming workflow, not merely look convincing in a demonstration. Demo Mode is an isolated test harness that can be moved or collapsed like any other dashboard card.

Connections are intentionally installation-specific. John's Twitch, OBS, Spotify, and Tangia credentials remain on John's computer. A tester authorizes their own accounts, and those credentials remain encrypted on the tester's computer. Credentials, reports, logs, and viewer data are never copied into a release ZIP or synchronized between installations.

For each completed change:

1. Verify the shared feature in development.
2. Ask John to approve the feature for release.
3. Build the owner version and tester version separately from the same approved source.
4. Check each artifact before it replaces the previous version.

Existing ZIP files are fixed snapshots and never update themselves.

## Release numbers

`RELEASE_VERSION.txt` is the single tester-package release number and uses the visible `.001`
format. An approved feature set keeps the same number in owner and tester builds. Increment it once
for the next approved release, then rebuild both channels. Tester ZIP filenames include `v001`,
`v002`, and so on, while the dashboard badge and health endpoint display `.001`, `.002`, and so on.
