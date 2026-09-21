# John's Twitch Handler release history

## .005 - 2026-09-21

- Added persistent left-side pages for **Pre-stream**, **During stream**, and **After stream** work.
- Grouped existing cards by stream phase without duplicating controls or losing saved card layouts.
- Automatically opens During stream when session tracking starts and After stream when it ends.
- Pauses card-specific browser polling while that card's page is inactive to conserve stream-PC resources.

## .004 - 2026-09-21

- Widened Preflight tiles and allowed labels and status details to wrap instead of being truncated.
- Let Preflight use the full session-card width so built-in and manual items remain readable.
- Moved a clearly labeled version badge beside the product name at the top of the dashboard.

## .003 - 2026-09-21

- Added locally saved manual Preflight reminders with **Get Coffee** as the first example.
- Manual reminders use checkboxes, affect readiness, and reset after a tracked session ends.
- Added **Open preflight list** so built-in checklist rows can be hidden without disabling functionality or changing readiness.
- Added a one-click option to restore every built-in Preflight field.

## .002 - 2026-09-21

- Added clear Yes / No controls for every optional integration.
- Added an optional, read-only TikFinity bridge that displays TikTok messages beside Twitch and YouTube chat.
- TikFinity defaults to No in owner and tester builds and performs no local connection work until enabled.
- Added TikFinity readiness to Preflight and the yellow, green, blue status-border rules.
- Kept TikTok credentials and account authorization entirely inside the separate TikFinity desktop app.

## .001 - 2026-09-21

First numbered owner and tester release.

- OBS scene, audio, microphone, replay, and performance controls.
- Twitch alerts, commands, ads, raids, viewer notes, and private chat chime.
- Optional Spotify song requests, queue overlay, Tangia controls, and StreamElements multistream planning.
- Twitch and embedded YouTube live chat in the dashboard and game popout.
- Safe manual and recurring actions for Twitch messages, OBS scenes, and stream markers.
- Persistent incoming and outgoing raid timelines for one week, two weeks, and one month.
- Movable, resizable, collapsible cards; themes; preflight; stream reports; and StreamLadder handoff.

The previously shared unnumbered Funk Test package belongs to this `.001` feature set. Testers should
use the newly stamped `v001` ZIP so the version is visible in the dashboard and diagnostics.
