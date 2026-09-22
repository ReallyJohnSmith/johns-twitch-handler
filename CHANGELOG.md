# John's Twitch Handler release history

## .012 - 2026-09-22

- Added **Add to Simple** to eligible Advanced panels, including Spotify song requests.
- Chosen panels appear directly in Simple mode while the rest of the technical dashboard stays hidden.
- Added a compact Simple-mode shortcut list and kept the older Chat, Spotify, and Ad choices compatible.
- Kept pin choices local to each installation so owner and tester layouts remain private and independent.
- Replaced tester ZIP distribution with one per-user Windows Setup executable that installs shortcuts and supports simple in-place updates.

## .011 - 2026-09-22

- Made panel-heading-to-panel-heading drops create tab stacks, matching the natural OBS gesture shown in John's recording.
- Made tab labels draggable so pulling a tab away separates it; releasing outside the dashboard turns it into a floating panel.
- Added an explicit **Unstack** button for users who prefer a predictable control instead of dragging.
- Added green **STACKED** and blue **UNSTACKED** confirmations that appear only after the layout actually changes.

## .010 - 2026-09-21

- Added a clear green **STACKED** confirmation after panels successfully become tabs.
- Prevented floating or unavailable panels from showing a misleading stack target.
- Changed the scene, microphone, and output readouts into neutral status displays, with separate plainly labeled **Scenes**, **Audio**, and **Chat** action buttons.

## .009 - 2026-09-21

- Reworked the During page around a fixed broadcast command strip for the current OBS scene, microphone state, output state, replay saving, and chat access.
- Changed the default Advanced layout hierarchy so scenes, audio, chat, alerts, clips, and ads take priority while optional integrations remain compact.
- Preserved genuinely customized panel orders while migrating the untouched older default to the broadcast-first arrangement.
- Made each Simple-mode personal checklist reminder a full-row clickable control with a clear checked state.
- Added OBS-style panel behavior: panels can float, move, resize, redock, and stack behind one another as tabs, with the arrangement retained locally.

## .008 - 2026-09-21

- Added personal Preflight reminders, including **Get Coffee**, directly to Simple mode.
- Added a compact, interpreted Stream Health summary without exposing raw metrics.
- Added two stage-aware shortcuts for setup, live OBS/audio controls, reports, and clips.
- Added **Customize Simple** so users can choose the core sections and optionally pin Chat, Song Requests, or the Ad Timer.
- Reworked the interface into a flatter, tighter broadcast-console style with less decorative UI.
- Added the During-stream status strip: **STREAM HEALTHY**, **PERFORMANCE PRESSURE**, or **STREAM AT RISK**.
- Prioritized current OBS controls and important audio while leaving raw metrics and integrations in Advanced.

## .007 - 2026-09-21

- Added default **Simple** mode with one interpreted status and one clear next action for each stream stage.
- Added **Advanced** mode that preserves the complete existing dashboard, raw metrics, diagnostics, and customization.
- Added plain-English Pre-stream readiness, During-stream health, and After-stream report summaries.
- Kept every current feature and setting on the same underlying local services without duplicating configuration.
- Added a Details control in Simple mode for progressively revealing the existing full interface.
- Made internet health part of the interpreted readiness result.

## .006 - 2026-09-21

- Added **Customize pages** so users choose which cards appear on Pre-stream, During stream, and After stream.
- Allows a card on multiple pages while preventing it from being removed from every page accidentally.
- Saves page assignments locally alongside the durable dashboard card order.
- Added one-click restoration of the recommended page assignments.

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

The previously shared unnumbered tester package belongs to this `.001` feature set. Testers should
use the newly stamped `v001` ZIP so the version is visible in the dashboard and diagnostics.
