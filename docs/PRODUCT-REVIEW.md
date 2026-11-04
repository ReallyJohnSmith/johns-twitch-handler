# Product review — November 1, 2026

The biggest opportunities are to make the first connection easier, recover clearly
when something is unavailable, and reduce dashboard work while it is out of view.
The before/during/after workflow and Simple mode are useful foundations to keep.

This review covers local development changes to the Windows app. They have not
replaced the installed app or created a numbered release. Website hosting remains
an exploratory idea; no website publishing or source upload is authorized.

## Improvements ready for review

| Problem found | Change | Benefit |
| --- | --- | --- |
| Setup reads like a list of requirements before someone can explore. | OBS is the first step, Twitch is described as optional, extra tools are collapsed, and **Look around first** closes setup. | Testers can understand the layout before entering accounts. |
| Finding the OBS password requires outside knowledge. | Expandable instructions name the OBS menu and **Show Connect Info**. | The next step is available beside the password field. |
| Connection buttons force horizontal scrolling in narrow Settings. | Fieldsets can shrink and connection buttons wrap onto another line. | The form stays readable at a 390-pixel viewport. |
| Connecting to OBS gives little feedback and permits repeated attempts. | A disabled **Connecting to OBS…** button and an adjacent result explain progress or failure. | Fewer accidental requests and less uncertainty. |
| A failed startup request can interrupt initialization without recovery. | A persistent notice offers **Try again** and setup help. Independent startup tasks settle separately; retry starts refresh loops only once. | A temporary failure does not require guessing whether to restart. |
| The first-run dialog can interrupt someone whose OBS is already connected or whose stream is active. | Automatic setup opens only on an unseen, disconnected, inactive first run. | Returning users can get straight to their controls. |
| Overlapping identical status reads repeat the same network work. | Plain reads of the same URL share one in-flight request, then are discarded. | Two simultaneous reads produce one request; later reads remain fresh. Writes are independent. |
| Polling timers continue waking while the tab is hidden. | Dashboard polling jobs are unscheduled while hidden and refreshed on return, without overlapping a running job. | Less background browser work. |
| Hidden diagnostic charts and an invisible audio mixer do unnecessary work. | Charts/process rows repaint only when visible; recurring mixer reads require visible controls. | The dashboard spends less effort on tools the user cannot see. |
| Hiding diagnostics can stop Simple mode from receiving current health. | System and internet snapshots refresh independently of the diagnostic panel. | Simple mode still receives current health while the dashboard is visible. |
| Failed health reads leave old healthy information visible. | Clear stale state, show **Unavailable**, and use **Review connection** on the live status strip. | A lost dashboard connection is not presented as a healthy stream or a diagnosed performance problem. |

## Internet monitoring stays

The Windows helper still owns internet checks, system sampling, session tracking,
and report storage. Browser visibility changes do not stop those services. The
existing internet probe waits five seconds between checks; the system sampler
waits three seconds between samples. No speed test or new monitoring service was
added. Closing the helper itself still stops its monitoring.

The visible dashboard refreshes health every three seconds after the previous
refresh finishes. Returning from a hidden tab requests fresh data. Failed status
reads have a 12-second timeout; write operations keep their existing semantics.

## Next priorities

1. **Make readiness match the user's setup.** Readiness currently expects Twitch
   events and NVIDIA Broadcast, even if a tester only wants OBS controls. Provide
   explicit choices for checks they actually use, preserve John's selected checks,
   and keep a configured-but-broken connection distinguishable from an unused one.
   Hiding a checklist row currently changes presentation, not these requirements.
2. **Verify the complete tester journey on another Windows computer.** Check a
   fresh install, an upgrade retaining settings/reports, OBS connection, and the
   local dashboard in Chrome and Edge. The isolated UI fixture cannot prove those
   work. Use a reviewed Windows installer for testers. If John later chooses a
   hosted website, its download and helper-pairing journey will need separate
   verification.
3. **Measure the helper during a real stream.** Profile CPU, memory, request counts,
   GPU-command launches, and local settings reads before changing service cadence
   or adding caches. GPU telemetry currently launches `nvidia-smi` during system
   sampling, and automatic connection checking revisits saved settings. These are
   candidates to measure, not proven bottlenecks. Keep internet/session recording
   running independently of an open browser.

## Verification and limits

- All **37 JavaScript regression checks passed**, including 13 runtime checks for
  request sharing, timeout/cancellation, polling suspension, retry, and fresh health.
- The Release solution build succeeded with **zero warnings and zero errors**.
- Browser checks used the real dashboard with an isolated server on port 5286:
  startup failure/retry, optional first-run setup, account-free exploration, OBS
  pending/error feedback, and unavailable health on the During page. No real
  accounts, OBS connection, installed settings, or monitoring were used.
- Layout was inspected at desktop and narrow widths. Browser checks showed no
  console errors in the fixture.
- An experimental website build was generated locally while exploring hosting.
  Hosting has not been chosen as the distribution plan. No website update was
  uploaded during this review; current work stays with the local Windows app.
- Request sharing and suspended polling are verified behavior. No CPU, battery,
  memory, game-FPS, or streaming-performance improvement has been benchmarked.
- Two existing browser-local countdown intervals still wake once per second;
  they skip rendering while hidden. The polling change does not claim zero work
  for the entire browser or Windows helper.

The prepared Windows release is **.016**. See the
[release workflow](../RELEASE_WORKFLOW.md) before creating owner/tester installers.
The separate [tester README](../README-FOR-TESTERS.md) remains the first-time guide;
this document is John's product review.
