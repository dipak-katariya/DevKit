@echo off
setlocal enabledelayedexpansion

set "ROOT=%~dp0"
set "WEBDIR=%ROOT%DevKit.Web"
set "PUBDIR=%ROOT%Release\app"
:: Packages have folders of their own in Release\ now: this is the one "Run Latest DevKit.bat" made the DevKit that runs.
set "LIVE_FILE=%ROOT%Release\live.txt"
:: Its first line names the package; it is used only when that line is a package's name, so it names a folder in Release\.
if exist "%LIVE_FILE%" set /p LIVE_PACKAGE=<"%LIVE_FILE%"
if defined LIVE_PACKAGE set LIVE_PACKAGE | findstr /r /x /c:"LIVE_PACKAGE=DevKit_[0-9][0-9]_[0-9][0-9]_[0-9][0-9][0-9][0-9]_[0-9][0-9]_[0-9][0-9]_[AP]M" >nul || set "LIVE_PACKAGE="
if defined LIVE_PACKAGE set "PUBDIR=%ROOT%Release\%LIVE_PACKAGE%\app"
set "PORT=5850"
set "URL=http://*:%PORT%"
set "LOCALURL=http://localhost:%PORT%"
set "APPNAME=DevKit"
set "SETSDK=%ROOT%tools\set-sdk.bat"

if "%~1"=="" goto :usage
if /i "%~1"=="run"       goto :run
if /i "%~1"=="host"      goto :host
if /i "%~1"=="local"     goto :local
if /i "%~1"=="publish"   goto :publish
if /i "%~1"=="autostart" goto :autostart
if /i "%~1"=="boot"      goto :boot
if /i "%~1"=="remove"    goto :remove
if /i "%~1"=="status"    goto :status
if /i "%~1"=="version"   goto :version
goto :usage

:run
:: Listens on every name of this machine. This machine's devkit.json (in %LOCALAPPDATA%\DevKit) decides the mode:
:: shared, where people open http://<this machine>:%PORT% and sign in with Windows, or (with no file) local.
set "START_URL=%URL%"
set "START_NOTE=DevKit prints where to open it once it is ready."
goto :start

:host
:: Cleans, restores and builds the Release build, then starts it shared on this machine.
call "%ROOT%scripts\devkit-host.bat"
goto :eof

:local
:: Only for the person who starts it, on this machine, opened with the link it prints.
set "DevKit__Mode=Local"
set "START_URL=%LOCALURL%"
set "START_NOTE=DevKit opens your browser with a sign-in link once it is ready."
goto :start

:start
echo.
echo  ╔══════════════════════════════════════╗
echo  ║       DevKit: Starting...           ║
echo  ╚══════════════════════════════════════╝
echo.

:: Check if published exe exists
if exist "%PUBDIR%\DevKit.Web.exe" (
    echo [*] Running from published build...
    echo [*] %START_NOTE%
    echo.
    cd /d "%PUBDIR%"
    DevKit.Web.exe --urls "%START_URL%"
    goto :eof
)

:: Check for .NET SDK
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [!] .NET SDK not found. Run 'devkit publish' first, then copy the Release folder.
    echo [!] Or install .NET 10 SDK from https://dotnet.microsoft.com/download
    pause
    goto :eof
)

if exist "%SETSDK%" (
    call "%SETSDK%" || (pause & goto :eof)
)
echo [*] Running from source (dotnet run)...
echo [*] %START_NOTE%
echo.
cd /d "%WEBDIR%"
dotnet run --urls "%START_URL%"
goto :eof

:publish
:: Makes a self-contained package in a folder of its own in Release\; "Run Latest DevKit.bat" there makes it the one that runs.
call "%ROOT%scripts\devkit-deploy.bat"
goto :eof

:autostart
echo [*] Adding %APPNAME% to Windows Startup...
if not exist "%PUBDIR%\DevKit.Web.exe" (
    echo [!] No published build found. Run 'devkit publish' first.
    pause
    goto :eof
)
:: Plain batch, no PowerShell: endpoint protection blocks PowerShell scripts that write to the Startup folder.
call "%ROOT%tools\devkit-startup.bat" %PORT% "%PUBDIR%" DevKit.Web.exe "--urls %URL%"
pause
goto :eof

:boot
:: Starts DevKit on %PORT% with the machine, before anyone signs in; asks for this account's password once.
call "%ROOT%tools\devkit-boot-task.bat" %PORT%
pause
goto :eof

:remove
set "STARTUP=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup"
set "REMOVED="
:: DevKit.cmd and DevKit.lnk are what DevKit put there before each port had a DevKit-<port>.cmd of its own.
for %%F in (DevKit-%PORT%.cmd DevKit.cmd DevKit.lnk) do if exist "%STARTUP%\%%F" del "%STARTUP%\%%F" && set "REMOVED=1"
if defined REMOVED (echo [OK] Removed from Startup.) else (echo [*] Not registered in Startup.)
schtasks /query /tn "DevKit %PORT%" >nul 2>&1 && (schtasks /delete /tn "DevKit %PORT%" /f >nul 2>&1 && echo [OK] Removed the start with the machine. || echo [!] The task "DevKit %PORT%" could not be removed: run this as administrator.)
pause
goto :eof

:status
echo.
echo --- %APPNAME% Status ---
set "STARTUP=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup"
set "REGISTERED="
for %%F in (DevKit-%PORT%.cmd DevKit.cmd DevKit.lnk) do if exist "%STARTUP%\%%F" set "REGISTERED=1"
if defined REGISTERED (echo [*] Autostart: REGISTERED) else (echo [*] Autostart: not registered)
schtasks /query /tn "DevKit %PORT%" >nul 2>&1 && (echo [*] Starts with the machine: YES) || (echo [*] Starts with the machine: NO)
if exist "%PUBDIR%\DevKit.Web.exe" (echo [*] Published: YES, in "%PUBDIR%") else (echo [*] Published: NO)
netstat -ano 2>nul | findstr ":%PORT% " >nul 2>&1
if not errorlevel 1 (echo [*] Running: YES on port %PORT%) else (echo [*] Running: NO)
echo.
pause
goto :eof

:version
for /f "tokens=2 delims=<>" %%v in ('findstr /i "<Version>" "%WEBDIR%\Directory.Build.props"') do (
    echo %APPNAME% v%%v
)
goto :eof

:usage
echo.
echo  DevKit: TFS workflow tools
echo  ────────────────────────────────────────
echo  Usage: devkit [command]
echo.
echo  Commands:
echo    host       Clean, restore, build and start it shared at http://%COMPUTERNAME%:%PORT% (scripts\devkit-host.bat)
echo    run        Start it as this machine's devkit.json says, without rebuilding
echo    local      Start it for yourself only (%LOCALURL%, with the printed link)
echo    publish    Make a new package in Release\, no .NET needed to run it (scripts\devkit-deploy.bat);
echo               "Run Latest DevKit.bat" in Release\ then swaps it in, with a maintenance page meanwhile
echo    autostart  Add to Windows Startup
echo    boot       Start with the machine, before anyone signs in (run as administrator)
echo    remove     Remove from Startup, and from the start with the machine
echo    status     Show status
echo    version    Show version
echo.
pause
goto :eof
