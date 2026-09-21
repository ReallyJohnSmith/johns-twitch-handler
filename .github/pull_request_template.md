## What changed

Describe the user-visible result and why it is needed.

## Verification

- [ ] `dotnet build StreamSidekick.sln --configuration Release` succeeds.
- [ ] `node --check src/StreamSidekick/wwwroot/app.js` succeeds.
- [ ] The owner workflow was checked locally.
- [ ] Optional integrations still perform no work when disabled.
- [ ] The change does not add unnecessary OBS, CPU, GPU, memory, or network load.
- [ ] No passwords, tokens, reports, viewer data, logs, or generated packages are included.
- [ ] User-facing errors explain what happened and what the streamer can do next.

## Release approval

- [ ] John approved the completed behavior for both owner and tester channels.
- [ ] `RELEASE_VERSION.txt`, `appsettings.json`, and `CHANGELOG.md` agree when this is a release.
- [ ] The owner and tester builds were produced from the same approved source.
