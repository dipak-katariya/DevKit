@echo off
REM Makes DevKit start when the machine starts, before anyone signs in, and keep running when its account signs out.
REM It adds a Windows scheduled task, "DevKit <port>", that runs as the account that runs this script a minute after
REM each start of the machine, whether that account is signed in or not, and starts the package DevKit-<port>.cmd in
REM its Startup folder names (tools\devkit-startup.bat writes it as DevKit starts). Windows asks for that account's
REM password once, to keep for the task; DevKit never sees it. Run it once, from a command prompt opened with "Run as
REM administrator", signed in as the account DevKit runs as, after DevKit has run on the port at least once:
REM   tools\devkit-boot-task.bat 5850
REM To stop it: schtasks /delete /tn "DevKit 5850" /f. It runs no PowerShell.
REM Exits 1, with the reason printed, when the task was not made.
setlocal
set "PORT=%~1"
if not defined PORT set "PORT=5850"
set "TASK=DevKit %PORT%"
set "LAUNCHER=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\DevKit-%PORT%.cmd"

echo %PORT%| findstr /r /x "[0-9][0-9]*" >nul || goto :badPort
if exist "%LAUNCHER%" goto :launcher
echo ERROR: "%LAUNCHER%" is missing. Start DevKit on port %PORT% once with "Run Latest DevKit.bat", then run this again.
exit /b 1

:badPort
echo ERROR: "%PORT%" is not a port.
exit /b 1

:launcher
REM Only an administrator may add a task that runs at startup: net session answers only in an elevated prompt.
net session >nul 2>&1
if not errorlevel 1 goto :elevated
echo ERROR: Run this from a command prompt opened with "Run as administrator".
exit /b 1

:elevated
echo Windows now asks for the password of %USERDOMAIN%\%USERNAME%, so the task can start DevKit before anyone signs in.
schtasks /create /tn "%TASK%" /tr "\"%LAUNCHER%\"" /sc onstart /delay 0001:00 /ru "%USERDOMAIN%\%USERNAME%" /rp * /rl limited /f
if errorlevel 1 goto :failed
echo DevKit on port %PORT% now starts with the machine, as %USERDOMAIN%\%USERNAME%, and keeps running after sign-out.
exit /b 0

:failed
echo ERROR: The task was not made; the lines above say why.
exit /b 1
