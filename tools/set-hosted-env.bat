@echo off
REM Checks what shared DevKit needs before it starts, then sets the environment it starts in. scripts\devkit-host.bat
REM calls it, and so does devkit-run.bat in a package that scripts\devkit-deploy.bat made, so both start DevKit the
REM same way. It sets DEVKIT_PORT (5850 unless already set), CONFIG (this machine's devkit.json), LIVE_STATE (beside
REM it: which package a switch made the one that runs on the port, see LivePackage.cs), DevKit__Mode,
REM ASPNETCORE_ENVIRONMENT and URLS (the address to pass DevKit, empty when devkit.json has its own). It exits 1, with
REM the reason printed, when devkit.json is missing or the port is in use (unless DEVKIT_PORT_HANDOVER is set).
if not defined DEVKIT_PORT set "DEVKIT_PORT=5850"

REM devkit.json sits beside the settings file, so DEVKIT_SETTINGS_PATH moves it too, as DevKit itself reads it.
set "CONFIG=%LOCALAPPDATA%\DevKit\devkit.json"
if defined DEVKIT_SETTINGS_PATH for %%F in ("%DEVKIT_SETTINGS_PATH%") do set "CONFIG=%%~dpFdevkit.json"
for %%F in ("%CONFIG%") do set "LIVE_STATE=%%~dpFlive-%DEVKIT_PORT%.txt"
if exist "%CONFIG%" goto :configured
echo ERROR: "%CONFIG%" is missing. Shared DevKit reads its TFS server from it.
echo Create it as "Shared on a server" in README.md describes, then run this again.
exit /b 1

:configured
REM While "Run Latest DevKit.bat" swaps packages, the port is in use on purpose: the maintenance page holds it until the
REM new DevKit takes it over.
if defined DEVKIT_PORT_HANDOVER goto :portfree
REM A running DevKit holds the port and locks its program files; stopping it is left to whoever started it.
netstat -ano | findstr /r /c:":%DEVKIT_PORT% .*LISTENING" >nul
if errorlevel 1 goto :portfree
echo ERROR: Port %DEVKIT_PORT% is already in use. Close the DevKit that is running, then run this again.
exit /b 1

:portfree
REM Shared mode whatever devkit.json says, and Production: no Development error pages or settings.
set "DevKit__Mode=Hosted"
set "ASPNETCORE_ENVIRONMENT=Production"
REM An endpoint in devkit.json's "Kestrel" section (https with a certificate) replaces the plain http address.
set "URLS=--urls http://*:%DEVKIT_PORT%"
findstr /i /c:"\"Kestrel\"" "%CONFIG%" >nul && set "URLS="
exit /b 0
