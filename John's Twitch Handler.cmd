@echo off
rem Main double-click launcher for John's Twitch Handler.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-streamsidekick.ps1"
