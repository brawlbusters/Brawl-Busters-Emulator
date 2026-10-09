@echo off
rem Plays with the patched client copy (tools\patch_client_relogin.py): after "Lost connection to game server" the
rem game stays open and shows the login box again, so you can log in without restarting it.
cd /d "%~dp0..\bin"
if not exist "pbclient_stay.exe" (
    echo pbclient_stay.exe is missing - run: python tools\patch_client_relogin.py
    pause
    exit /b 1
)
start "" "pbclient_stay.exe" -Publisher "SG" -Language "EN" -ServerGroup "AS_ID_Internal"
