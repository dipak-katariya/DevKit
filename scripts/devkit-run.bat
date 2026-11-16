@echo off
REM Runs DevKit shared from the package this script sits in, and keeps it running. devkit-deploy.bat makes each package
REM in a folder of its own in Release\ and copies this script into it; the switch ("Run Latest DevKit.bat" there) starts
REM it in a minimized window. It runs on the machine that hosts DevKit, signed in as the account DevKit should run as:
REM it reads that account's devkit.json and checks the port as devkit-host.bat does (see "Run it on another machine" in
REM README.md). When DevKit stops with an error, it is started again after a pause. When it keeps stopping soon after
REM it starts, something needs fixing first, so this script stops too. Ctrl+C, then Y, stops both. Once a switch has
REM made another package, or another run of this one, the one that runs (the live state beside devkit.json names both,
REM for every Release folder on this machine), this script stops instead of starting its DevKit again.
REM Each start and stop is also written to devkit-run.log beside devkit.json. It also puts DevKit.cmd in that account's
REM Startup folder (tools\devkit-startup.bat), which starts the package the live state names at each sign-in. It runs
REM no PowerShell, which endpoint protection such as Cortex XDR may block.
REM Pass --no-pause to skip the closing pause when nobody is watching; the switch does.
setlocal
title DevKit
set "APP=%~dp0app"
for %%P in ("%~dp0.") do set "PACKAGE_DIR=%%~fP"
set "RC=0"
set "LOG="
set "RESTART_DELAY_SECONDS=30"
REM A run at least this long counts as working, so a stop after it starts the count of quick failures again.
set "STEADY_RUN_SECONDS=300"
set "MAX_QUICK_FAILURES=5"
set "QUICK_FAILURES=0"
set "SECONDS_PER_DAY=86400"

if exist "%APP%\DevKit.Web.exe" goto :packaged
echo ERROR: "%APP%\DevKit.Web.exe" is missing. Run devkit-run.bat from a package that devkit-deploy.bat made.
set "RC=1"
goto :done

:packaged
call "%~dp0tools\set-hosted-env.bat"
if errorlevel 1 goto :refused
for %%F in ("%CONFIG%") do set "LOG=%%~dpFdevkit-run.log"
REM The run that started this script: the switch's, or, when started at sign-in, the one the live state names for this
REM package. Its DevKit gets it too, to take the port from that run's maintenance page and to make way for the next.
set "MY_RUN=%DEVKIT_RUN_ID%"
if not defined MY_RUN call :adoptRun
set "DEVKIT_RUN_ID=%MY_RUN%"
REM A test or side-by-side copy, with a settings location of its own, is never started by itself at sign-in. A
REM Startup file that could not be written is reported and DevKit starts anyway.
if not defined DEVKIT_SETTINGS_PATH call "%~dp0tools\devkit-startup.bat" %DEVKIT_PORT% "%~dp0" %~nx0 "--no-pause" "%LIVE_STATE%"
REM DevKit serves its pages from the folder it starts in.
cd /d "%APP%"

:start
call :seconds STARTED
call :log "Starting DevKit shared on port %DEVKIT_PORT% from %APP%"
"%APP%\DevKit.Web.exe" %URLS%
set "RC=%errorlevel%"
call :log "DevKit stopped with exit code %RC%"
call :replaced && exit /b 0
if "%RC%"=="0" goto :done
call :seconds STOPPED
set /a "RAN=(STOPPED-STARTED+SECONDS_PER_DAY)%%SECONDS_PER_DAY"
if %RAN% GEQ %STEADY_RUN_SECONDS% (set "QUICK_FAILURES=0") else (set /a "QUICK_FAILURES+=1")
if %QUICK_FAILURES% LSS %MAX_QUICK_FAILURES% goto :restart
call :log "DevKit stopped with an error %MAX_QUICK_FAILURES% times in a row, each within %STEADY_RUN_SECONDS% seconds of starting, so it is not started again. Fix what its messages above say, then run Run Latest DevKit.bat again."
goto :done

:restart
call :log "Starting DevKit again in %RESTART_DELAY_SECONDS% seconds"
REM Ping waits a second between replies, and unlike timeout it also waits when input is redirected.
set /a "PINGS=RESTART_DELAY_SECONDS+1"
ping -n %PINGS% 127.0.0.1 >nul
call :replaced && exit /b 0
goto :start

:refused
set "RC=1"

:done
echo.
if /i not "%~1"=="--no-pause" pause
exit /b %RC%

REM Sets the variable named %1 to the seconds since midnight. A run across midnight still measures right, as RAN wraps
REM at a day; one longer than a day may count as quick, which costs at most one restart of the five allowed.
REM The hour has a leading space before 10, and the 1xx-100 form keeps 08 and 09 from being read as octal.
:seconds
for /f "tokens=1-3 delims=:.," %%A in ("%time: =0%") do set /a "%~1=(1%%A-100)*3600+(1%%B-100)*60+(1%%C-100)"
exit /b 0

REM Exits 0, logging it, when the live state names another package, or another run of this one: a switch has made that
REM the DevKit that runs. Without a live state, as for a package run on its own, this one stays.
:replaced
call :readLive
if not defined LIVE exit /b 1
if /i "%LIVE%"=="%PACKAGE_DIR%" goto :samePackage
call :log "%LIVE% runs now instead, so DevKit from %PACKAGE_DIR% stays stopped"
exit /b 0

:samePackage
if "%LIVE_RUN%"=="%MY_RUN%" exit /b 1
call :log "%PACKAGE_DIR% was started again by a switch, so this earlier run of it stays stopped"
exit /b 0

REM Sets LIVE and LIVE_RUN from the live state: the package that runs, and the run that started it.
:readLive
set "LIVE="
set "LIVE_RUN="
if not exist "%LIVE_STATE%" exit /b 0
set /p LIVE=<"%LIVE_STATE%"
for /f "usebackq skip=1 delims=" %%R in ("%LIVE_STATE%") do if not defined LIVE_RUN set "LIVE_RUN=%%R"
call :unsafeLive || exit /b 0
set "LIVE="
set "LIVE_RUN="
call :log "%LIVE_STATE% holds characters a switch never writes there, so this script reads no live state from it"
exit /b 0

REM Exits 0 when LIVE or LIVE_RUN holds a character cmd would read as part of a command or a variable (" & | < > ^ %),
REM which a switch never writes: they are compared and logged with %%, so such a value could run a command. They are
REM looked at here only through delayed expansion, which never reads a value as a command.
:unsafeLive
setlocal EnableDelayedExpansion
set "TEXT=!LIVE!#!LIVE_RUN!"
set "KEPT=!TEXT:&=!"
set "KEPT=!KEPT:|=!"
set "KEPT=!KEPT:<=!"
set "KEPT=!KEPT:>=!"
set "KEPT=!KEPT:^=!"
set "KEPT=!KEPT:%%=!"
set "KEPT=!KEPT:"=!"
set "UNSAFE=1"
if "!KEPT!"=="!TEXT!" set "UNSAFE="
endlocal & set "UNSAFE=%UNSAFE%"
if defined UNSAFE exit /b 0
exit /b 1

REM Takes the run the live state names when it names this package, for a start without a switch, as at sign-in.
:adoptRun
call :readLive
if /i "%LIVE%"=="%PACKAGE_DIR%" set "MY_RUN=%LIVE_RUN%"
exit /b 0

REM Prints %1 and adds it to devkit-run.log. Delayed expansion prints it as it is: a folder name with & or ) in it is
REM not read as a command. A log past 5 MB becomes devkit-run.1.log, replacing the one before, so it never fills the disk.
:log
set "LOG_LINE=[%date% %time:~0,8%] %~1"
if defined LOG if exist "%LOG%" for %%L in ("%LOG%") do if %%~zL GTR 5242880 move /y "%%~fL" "%%~dpnL.1%%~xL" >nul 2>&1
setlocal EnableDelayedExpansion
echo(!LOG_LINE!
if defined LOG >>"!LOG!" echo(!LOG_LINE!
endlocal
exit /b 0
