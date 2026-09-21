@echo off
rem Double-click entry point for nontechnical testers.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Launch John's Twitch Handler.ps1"
