@echo off
setlocal enabledelayedexpansion

set "ROOT=%~dp0"
set "WEBDIR=%ROOT%DevKit.Web"
set "PUBDIR=%WEBDIR%\publish"
set "PORT=5850"
set "URL=http://localhost:%PORT%"
set "APPNAME=DevKit"

if "%~1"=="" goto :usage
if /i "%~1"=="run"       goto :run
if /i "%~1"=="publish"   goto :publish
if /i "%~1"=="autostart" goto :autostart
if /i "%~1"=="remove"    goto :remove
if /i "%~1"=="status"    goto :status
if /i "%~1"=="version"   goto :version
goto :usage

:run
echo.
echo  ╔══════════════════════════════════════╗
echo  ║       DevKit — Starting...          ║
echo  ╚══════════════════════════════════════╝
echo.

:: Check if published exe exists
if exist "%PUBDIR%\DevKit.Web.exe" (
    echo [*] Running from published build...
    echo [*] URL: %URL%
    echo.
    start "" "%URL%"
    cd /d "%PUBDIR%"
    DevKit.Web.exe --urls "%URL%"
    goto :eof
)

:: Check for .NET SDK
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [!] .NET SDK not found. Run 'devkit publish' first, then copy the publish folder.
    echo [!] Or install .NET 9 SDK from https://dotnet.microsoft.com/download
    pause
    goto :eof
)

echo [*] Running from source (dotnet run)...
echo [*] URL: %URL%
echo.
start "" "%URL%"
cd /d "%WEBDIR%"
dotnet run --urls "%URL%"
goto :eof

:publish
echo.
echo [*] Publishing self-contained build (win-x64)...
echo.
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [!] .NET SDK required for publishing. Install from https://dotnet.microsoft.com/download
    pause
    goto :eof
)
cd /d "%WEBDIR%"
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o "%PUBDIR%" 2>&1
if errorlevel 1 (
    echo.
    echo [!] Publish FAILED.
    pause
    goto :eof
)
echo.
echo [OK] Published to: %PUBDIR%
echo [OK] Copy this folder to any Windows machine — no .NET required.
echo [OK] Run: DevKit.Web.exe --urls "%URL%"
echo.
pause
goto :eof

:autostart
echo [*] Adding %APPNAME% to Windows Startup...
set "STARTUP=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup"
set "SHORTCUT=%STARTUP%\DevKit.lnk"
if exist "%PUBDIR%\DevKit.Web.exe" (
    set "TARGET=%PUBDIR%\DevKit.Web.exe"
    set "ARGS=--urls %URL%"
    set "WORKDIR=%PUBDIR%"
) else (
    echo [!] No published build found. Run 'devkit publish' first.
    pause
    goto :eof
)
:: Create shortcut via PowerShell
powershell -NoProfile -Command "$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut('%SHORTCUT%'); $s.TargetPath = '%TARGET%'; $s.Arguments = '%ARGS%'; $s.WorkingDirectory = '%WORKDIR%'; $s.WindowStyle = 7; $s.Save()"
echo [OK] Shortcut created: %SHORTCUT%
pause
goto :eof

:remove
set "SHORTCUT=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\DevKit.lnk"
if exist "%SHORTCUT%" (
    del "%SHORTCUT%"
    echo [OK] Removed from Startup.
) else (
    echo [*] Not registered in Startup.
)
pause
goto :eof

:status
echo.
echo --- %APPNAME% Status ---
set "SHORTCUT=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\DevKit.lnk"
if exist "%SHORTCUT%" (echo [*] Autostart: REGISTERED) else (echo [*] Autostart: not registered)
if exist "%PUBDIR%\DevKit.Web.exe" (echo [*] Published: YES — %PUBDIR%) else (echo [*] Published: NO)
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
echo  DevKit — Branch Creator ^& Merge Tool
echo  ────────────────────────────────────────
echo  Usage: devkit [command]
echo.
echo  Commands:
echo    run        Start the application (%URL%)
echo    publish    Build self-contained exe (no .NET needed)
echo    autostart  Add to Windows Startup
echo    remove     Remove from Startup
echo    status     Show status
echo    version    Show version
echo.
pause
goto :eof
