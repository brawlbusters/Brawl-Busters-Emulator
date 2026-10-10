@echo off
rem Builds the client check module (32-bit, like the game) as LightFX.dll in this folder.
rem Needs Visual Studio 2022 with the C++ tools. Copy the result next to pbclient.exe (the bin folder).
setlocal
cd /d "%~dp0"
set "VCVARS=%ProgramFiles%\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars32.bat"
if not exist "%VCVARS%" set "VCVARS=%ProgramFiles%\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars32.bat"
if not exist "%VCVARS%" (
    echo vcvars32.bat not found - install the C++ tools of Visual Studio 2022.
    exit /b 1
)
call "%VCVARS%" >nul
cl /nologo /W3 /O2 /MT /LD client_check.c /Fe:LightFX.dll /link /NOLOGO ws2_32.lib bcrypt.lib
if errorlevel 1 exit /b 1
del client_check.obj LightFX.exp LightFX.lib 2>nul
echo Built %~dp0LightFX.dll
endlocal
