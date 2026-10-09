@echo off
rem Starts the server for play on this PC (127.0.0.1) - config\emulator.local.json.
rem start-servers.bat alone uses config\emulator.json, which advertises the public address.
set BRAWLBUSTERS_CONFIG=%~dp0config\emulator.local.json
call "%~dp0start-servers.bat"
