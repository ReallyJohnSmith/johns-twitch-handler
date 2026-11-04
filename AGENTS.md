# Twitch Helper — John's Twitch Handler

## Project scope

This repository contains the Windows Twitch helper and its related website source.
Treat this directory as the project root. `StreamSidekick` remains the internal
solution and application name.

- Keep streaming integrations, application settings, build output, tests, and
  documentation within this project.
- The personal calendar lives in the sibling `our-days-calendar` repository.
  Do not edit that project as part of Twitch helper work unless the user explicitly
  requests work on both projects.
- Do not introduce cross-repository source imports or shared runtime data between
  the Twitch helper and personal calendar.
- The sibling `Johns-Twitch-Handler-Web` and `Johns-Twitch-Handler-Demo` folders
  contain separate website output checkouts; they belong to Twitch work, not the
  personal calendar. Use the existing website workflow when updating them.

## Working here

- Read `PROJECT_NOTES.md`, `CONTRIBUTING.md`, and `RELEASE_WORKFLOW.md` before
  changing application behavior or packaging.
- Use `StreamSidekick.sln` and the build instructions in `CONTRIBUTING.md`.
- Main application source is in `src/StreamSidekick`; website source is in `website/`.
- Preserve existing local work and keep changes scoped to the requested task.
- Hosting remains exploratory; project organization does not authorize publishing.
