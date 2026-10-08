@echo off
cd /d "%~dp0"

dotnet build BrawlBusters.sln -c Release -nologo -v q
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)

start "Brawl Busters - AuthServer" dotnet run --project src\BrawlBusters.AuthServer -c Release --no-build
start "Brawl Busters - MainServer" dotnet run --project src\BrawlBusters.MainServer -c Release --no-build
start "Brawl Busters - CastServer" dotnet run --project src\BrawlBusters.CastServer -c Release --no-build
