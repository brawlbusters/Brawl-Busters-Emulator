@echo off
setlocal
cd /d "%~dp0"

rem ---- the private MariaDB under database\server, if it has been set up
if exist "database\server\data\my.ini" (
    netstat -ano | findstr /r /c:":3306 .*LISTENING" >nul
    if errorlevel 1 (
        for /d %%D in ("database\server\mariadb-*-winx64") do set "MARIADB_HOME=%%D"
        if defined MARIADB_HOME (
            echo Starting MariaDB...
            start "Brawl Busters - MariaDB" /min "%MARIADB_HOME%\bin\mariadbd.exe" "--defaults-file=%~dp0database\server\data\my.ini"
            timeout /t 4 /nobreak >nul
        )
    ) else (
        echo MariaDB is already running.
    )
)

dotnet build BrawlBusters.sln -c Release -nologo -v q
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)

start "Brawl Busters - Server" dotnet run --project src\BrawlBusters.Server -c Release --no-build
endlocal
