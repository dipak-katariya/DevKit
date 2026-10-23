@echo off
REM Runs DevKit shared from the package this script sits in, and keeps it running. devkit-deploy.bat makes the package
REM and copies this script into it. Run it on the machine that hosts DevKit, signed in as the account DevKit should run
REM as: it reads that account's devkit.json and checks the port as devkit-host.bat does (see "Run it on another
REM machine" in README.md). When DevKit stops with an error, it is started again after a pause. When it keeps stopping
REM soon after it starts, something needs fixing first, so this script stops too. Ctrl+C, then Y, stops both.
REM Each start and stop is also written to devkit-run.log beside devkit.json. It also puts DevKit.cmd, which starts it,
REM in that account's Startup folder (tools\devkit-startup.bat), so DevKit starts again by itself at each sign-in. It
REM runs no PowerShell, which endpoint protection such as Cortex XDR may block.
REM Pass --no-pause to skip the closing pause when nobody is watching.
setlocal
set "APP=%~dp0app"
set "RC=0"
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
REM A test or side-by-side copy, with a settings location of its own, is never started by itself at sign-in. A
REM Startup file that could not be written is reported and DevKit starts anyway.
if not defined DEVKIT_SETTINGS_PATH call "%~dp0tools\devkit-startup.bat" %DEVKIT_PORT% "%~dp0" %~nx0
REM DevKit serves its pages from the folder it starts in.
cd /d "%APP%"

:start
call :seconds STARTED
call :log "Starting DevKit shared on port %DEVKIT_PORT% from %APP%"
"%APP%\DevKit.Web.exe" %URLS%
set "RC=%errorlevel%"
call :log "DevKit stopped with exit code %RC%"
if "%RC%"=="0" goto :done
call :seconds STOPPED
set /a "RAN=(STOPPED-STARTED+SECONDS_PER_DAY)%%SECONDS_PER_DAY"
if %RAN% GEQ %STEADY_RUN_SECONDS% (set "QUICK_FAILURES=0") else (set /a "QUICK_FAILURES+=1")
if %QUICK_FAILURES% LSS %MAX_QUICK_FAILURES% goto :restart
call :log "DevKit stopped with an error %MAX_QUICK_FAILURES% times in a row, each within %STEADY_RUN_SECONDS% seconds of starting, so it is not started again. Fix what its messages above say, then run devkit-run.bat again."
goto :done

:restart
call :log "Starting DevKit again in %RESTART_DELAY_SECONDS% seconds"
REM Ping waits a second between replies, and unlike timeout it also waits when input is redirected.
set /a "PINGS=RESTART_DELAY_SECONDS+1"
ping -n %PINGS% 127.0.0.1 >nul
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

:log
echo [%date% %time:~0,8%] %~1
>>"%LOG%" echo [%date% %time:~0,8%] %~1
exit /b 0
