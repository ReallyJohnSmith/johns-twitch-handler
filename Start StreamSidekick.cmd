@echo off
rem Legacy shortcut retained so older pinned links still open John's Twitch Handler.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-streamsidekick.ps1"
 
