# Website idea — John's concept README

**Exploratory prototype only.** John has not chosen a hosted website as the
distribution plan. The URLs and implementation notes below record earlier
exploration; they are not an approved entry point for testers. Current product
improvements stay local. Do not publish or upload updates without John's explicit
instruction to do so.

This is the review copy for John. The separate
[tester README](../README-FOR-TESTERS.md) is written for someone trying the tool
for the first time.

## What a new visitor sees

1. **An introduction**, rather than a helper-pairing form. The homepage explains
   what the Handler does and offers **Try the interactive demo** first.
2. **A small, interactive tour** with four steps: readiness, sample controls,
   connection warnings/recovery, and a session recap. No installation or account
   connection is needed. Visitors can jump between steps or reset the demo.
3. **A Windows-app path** with three short setup steps, a complete guide, and a
   downloadable tester README.
4. **Help and feedback** in ordinary language. The feedback button copies a
   checklist; it does not submit anything or require GitHub sign-in.

The existing website-connected dashboard moves to `dashboard.html`. The
**Already installed?** link opens it. The Windows app's local dashboard and real
controls remain in their original source; this change does not make its control
buttons act like sample buttons.

## Local review

The website remains an exploratory prototype. The Windows release has no hosted
website configured. Use the local website build for review; choose and verify a
distribution address before enabling website connections in a future package.

## How the demo behaves

- Homepage scene buttons change a clearly labeled sample preview.
- **Run sample checks** marks the three sample readiness items ready.
- Scene selection, microphone toggling, highlight marking, and replay simulation
  update only in-memory example state. The demo creates no video files.
- **Simulate a hiccup** shows a 186 ms example; **Recover connection** records a
  26 ms example. Those are illustrations, not measurements of the visitor's PC.
- The recap reflects the visitor's sample actions. Its event list is bounded.
- **Start over** clears the demo. Reloading starts fresh too.
- The demo loads only local static assets. It uses no account APIs, helper
  transport, browser storage, analytics, or feedback submission.
- Clipboard buttons copy text only when clicked and offer selectable text if
  clipboard access is unavailable.

## Installer availability — still needs a distribution decision

No public GitHub installer URL could be verified: the unauthenticated Releases
API returned 404 on October 31, 2026. This may reflect repository access settings;
it does not prove that no private release exists.

The homepage therefore uses **installer by invitation** instructions. It does
not offer a fake Download button or send first-time visitors to an unverified
release page. The previously built development helper installer is not promoted
as an approved release.

When a reviewed installer is accessible to the intended testers, set
`installerUrl` in [welcome-config.js](welcome-config.js) to its actual HTTPS
download URL. A valid configured URL reveals **Download for Windows** and updates
the availability text. It must be verified separately for the intended audience;
source configuration cannot grant access to a private GitHub release.

Website publishing is independent from Windows installer release approval. The
existing [release workflow](../RELEASE_WORKFLOW.md) still applies to a numbered
tester build. Clean-install, upgrade, Windows startup, and real OBS/account
checks remain separate from this static-site onboarding change.

## Things for John to review

- Does the homepage explain why a streamer would use this?
- Can a new visitor spot **Try the interactive demo** immediately?
- Are the four demo steps clear without you talking someone through them?
- Does sample mode stay distinct from controls for a real stream?
- Are the tester README and installation guide short enough to start with?
- What installer/release should testers receive, and who should access the Site?

## Source and rebuild

The files in this `website/` folder are the source for the homepage and isolated
demo. The website build copies an explicit allowlist of public assets and the
tester README, and generates `dashboard.html` from the existing app interface.
Keep runtime data, account credentials, installers, and local settings out of
the website output.

```powershell
.\build-website.ps1 -DestinationDirectory ..\Johns-Twitch-Handler-Web\dist
```

The installed app's web assets are under `src/StreamSidekick/wwwroot`. The
connected dashboard continues using the existing authenticated local-helper
transport. The new landing page and demo do not load that transport.

The tester README is also copied into future installer payloads by
`build-tester-package.ps1`; an existing installer is unchanged until rebuilt.
The website and future installer payloads include a plain-text `.txt` version so
testers can open the instructions directly in Notepad. The source stays Markdown.

## Verification, October 31, 2026

- Website builds successfully from the source allowlist.
- Syntax checks pass for the new homepage and demo JavaScript.
- 11 existing tester-guide and website-connection checks pass. The prose-branding
  check now ignores URL addresses so the legitimate hosting hostname does not
  fail that check; it also covers the new tester README.
- The generated pages' local asset links, anchors, and IDs were checked. The
  generated connected dashboard still loads its existing helper transport.
- Desktop browser walkthrough verified the homepage sample scene control, all
  four demo steps, microphone toggle, highlight/replay counters, warning/recovery
  events, final recap, and a complete reset. No console errors were observed.
- The homepage and demo were visually checked in 390-pixel-wide preview frames.
- Windows installer rebuilding, live helper pairing, and account actions are
  outside this static-site change and retain their previous verification status.
