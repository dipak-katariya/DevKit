@echo off
REM "Run Latest DevKit.bat": scripts\devkit-deploy.bat puts this script in Release\, beside the packages it makes, each in
REM a folder named after the time it was made, DevKit_<day>_<month>_<year>_<hour>_<minute>_<AM|PM>. It makes the newest
REM package the DevKit that runs, and whoever opens DevKit meanwhile finds a maintenance page rather than nothing. The
REM switch itself is that package's DevKit.Web.exe --maintenance (DevKitSwitch.cs), and it runs in this window:
REM   1. It takes the port for one switch at a time, and its maintenance page gets ready, with DevKit's own address and
REM      certificate. Nothing changes when it cannot, so a damaged package never takes DevKit down.
REM   2. The DevKit that runs is asked to make way, and finishes the requests it is serving; one from a package made
REM      before it could be asked is stopped. The page takes the port at once: DevKit is being updated, it tells everyone.
REM   3. The package starts in a minimized "DevKit" window, kept running by its devkit-run.bat. As it is about to listen,
REM      the page makes way, so the port is closed for a moment only.
REM   4. When it does not start, the package that ran before starts again, and this one is marked as failed
REM      (switch-failed.txt in it), so that running this again skips it.
REM Running this again with no name restarts the newest package that has not failed and has a package.txt that matches
REM its files. To pick one, pass its folder name:
REM   "Run Latest DevKit.bat" DevKit_28_10_2026_11_02_AM
REM That tries a failed package again too, and is the only way to pick a package without package.txt: one made before
REM DevKit checked its packages, or one copied here only in part, and DevKit_Previous, the single package an earlier
REM devkit-deploy.bat made right in this folder. The switch then checks every file of the package against package.txt,
REM and refuses a package, or a Release folder, that an account other than this one, SYSTEM and administrators may
REM change (tools\devkit-lock.bat locks them). Which package runs is kept per account and port beside devkit.json, so
REM every Release folder on the machine follows it, and so does the Startup file at sign-in.
REM Run it on the machine that hosts DevKit, signed in as the account DevKit runs as. It runs no PowerShell.
REM Pass --no-pause to skip the closing pause when nobody is watching, and --yes to go back without being asked to a
REM package made before DevKit kept the settings it does not know.
setlocal
set "RELEASE=%~dp0"
set "RELEASE=%RELEASE:~0,-1%"
set "SINGLE_PACKAGE=DevKit_Previous"
set "NAME_PATTERN=DevKit_[0-9][0-9]_[0-9][0-9]_[0-9][0-9][0-9][0-9]_[0-9][0-9]_[0-9][0-9]_[AP]M"
REM DevKit.Web.exe ended before it ran: its runtime could not start it from a package missing files.
set "HOST_FAILED=-2147450"
REM An error the switch did not expect ended it on the way.
set "CRASHED=-532462766"
set "KEEP_PACKAGES=10"
set "RC=0"
set "NO_PAUSE="
set "YES="
set "PACKAGE_NAME="
set "LOG="

:arguments
if "%~1"=="" goto :parsed
if /i "%~1"=="--no-pause" (set "NO_PAUSE=1") else if /i "%~1"=="--yes" (set "YES=1") else set "PACKAGE_NAME=%~1"
shift
goto :arguments

:parsed
set "CHOSEN="
set "CHOSEN_KEY="
REM The newest whole package made with this switch that has not failed: it runs the switch to an older package.
set "SWITCHER="
set "SWITCHER_KEY="
for /d %%P in ("%RELEASE%\DevKit_*") do call :consider "%%~nxP"
if not defined PACKAGE_NAME goto :picked
call :inspect "%PACKAGE_NAME%"
if defined WHOLE (set "CHOSEN=%CANDIDATE%") else set "CHOSEN="

:picked
if defined CHOSEN goto :chosen
if defined PACKAGE_NAME echo ERROR: "%PACKAGE_NAME%" is not a whole DevKit package in "%RELEASE%". Name one of the DevKit_ folders there.
if not defined PACKAGE_NAME echo ERROR: "%RELEASE%" holds no package that has not failed and whose package.txt matches its files. Make one with scripts\devkit-deploy.bat, or name one.
set "RC=1"
goto :done

:chosen
call :inspect "%CHOSEN%"
set "SWITCH_WITH=%SWITCHER%"
if defined CURRENT set "SWITCH_WITH=%CHOSEN%"
if defined SWITCH_WITH goto :switcher
echo ERROR: No package in "%RELEASE%" can run the switch: they were all made before it. Make one with scripts\devkit-deploy.bat.
set "RC=1"
goto :done

:switcher
if defined FAILED (
    echo %CHOSEN% failed before, so it is tried again only because it was named:
    type "%RELEASE%\%CHOSEN%\switch-failed.txt"
)
if not defined CURRENT call :confirmOlder || goto :refused
if /i "%CHOSEN%"=="%SINGLE_PACKAGE%" call :refreshSingle || goto :refused
set "PACKAGE=%RELEASE%\%CHOSEN%"
set "SWITCH_APP=%RELEASE%\%SWITCH_WITH%\app"
REM The port is in use on purpose until the new DevKit takes it over: by the DevKit that runs, then the maintenance
REM page. The new package's devkit-run.bat inherits this too.
set "DEVKIT_PORT_HANDOVER=1"
call "%RELEASE%\%SWITCH_WITH%\tools\set-hosted-env.bat"
if errorlevel 1 goto :refused
for %%F in ("%CONFIG%") do (
    set "LOG=%%~dpFdevkit-run.log"
    set "STATE_DIR=%%~dpF"
)

call :listener RUNNING
set "STOP="
if not defined RUNNING goto :switch
call :image %RUNNING%
if /i "%IMAGE%"=="DevKit.Web.exe" (set "STOP=--stop %RUNNING%") else goto :notDevKit

:switch
call :retireSingleLoop
call :log "Switching DevKit on port %DEVKIT_PORT% to %CHOSEN%"
echo.
"%SWITCH_APP%\DevKit.Web.exe" --maintenance --package "%PACKAGE%" %STOP% %URLS%
set "RC=%errorlevel%"
echo.
if "%RC%"=="0" goto :switched
if "%RC%"=="4" goto :wentBack
if "%RC%"=="5" goto :down
if "%RC%"=="6" goto :down
if "%RC%"=="7" goto :interrupted
if "%RC%"=="%CRASHED%" goto :crashed
call :restoreSingleLoop
if "%RC%"=="1" goto :unchanged
if "%RC%"=="2" goto :busy
if "%RC%"=="3" goto :unchanged
set "RC_START=%RC:~0,8%"
if "%RC_START%"=="%HOST_FAILED%" goto :damaged
goto :crashed

:switched
call :log "%CHOSEN% runs on port %DEVKIT_PORT%"
call :retireSinglePackage
call :removeOldPackages
echo DevKit from %CHOSEN% now runs on port %DEVKIT_PORT%, kept running by the minimized "DevKit" window.
goto :done

:wentBack
call :log "ERROR: %CHOSEN% did not start, so the package that ran before runs again"
echo ERROR: DevKit from %CHOSEN% did not start, so the package that ran before runs again.
echo %CHOSEN% is marked as failed, so running this again skips it; to try it again, pass its name.
echo The lines above, and "%LOG%", say why it did not start.
goto :done

:down
call :log "ERROR: %CHOSEN% did not start, and no earlier package runs instead: DevKit is down on port %DEVKIT_PORT%"
echo ERROR: DevKit from %CHOSEN% did not start, and no earlier package runs instead, so DevKit is down.
echo The lines above, and "%LOG%", say why. Fix that and run this again, or pass the name of a package that worked.
goto :done

:interrupted
call :log "The switch to %CHOSEN% was stopped; that DevKit goes on starting by itself"
echo The switch was stopped. DevKit from %CHOSEN% goes on starting in its minimized "DevKit" window.
goto :done

:unchanged
call :log "The switch to %CHOSEN% changed nothing (exit code %RC%)"
echo Nothing was changed: the lines above say why.
goto :done

:busy
echo Nothing was changed: another switch on port %DEVKIT_PORT% is still running in another window. Wait for it to
echo finish, then run this again if needed.
goto :done

:damaged
call :log "ERROR: DevKit.Web.exe from %SWITCH_WITH% did not start (exit code %RC%), so nothing was changed"
echo ERROR: DevKit.Web.exe from %SWITCH_WITH% did not start, so nothing was changed: its files are damaged or missing.
>"%RELEASE%\%SWITCH_WITH%\switch-failed.txt" echo Its DevKit.Web.exe did not start (exit code %RC%).
echo It is marked as failed, so running this again skips it. Make a new package with scripts\devkit-deploy.bat.
goto :done

:crashed
call :log "ERROR: The switch to %CHOSEN% stopped on an error (exit code %RC%)"
echo ERROR: The switch stopped on an error the lines above show. DevKit may not be running: run this again.
goto :done

:notDevKit
echo ERROR: Port %DEVKIT_PORT% is in use by %IMAGE% (process %RUNNING%), which is not DevKit. Stop it, then run this again.

:refused
set "RC=1"

:done
if not "%RC%"=="0" set "RC=1"
echo.
if not defined NO_PAUSE pause
exit /b %RC%

REM Takes package folder %1 as CHOSEN and SWITCHER when its package.txt matches its files, it has not failed, and it is
REM newer than them. A package without package.txt is never picked so: it may be one that is still being copied or
REM made, or was copied only in part. DevKit_Previous has no date, so it is never picked so either.
:consider
call :inspect "%~1"
if not defined CURRENT exit /b 0
if defined FAILED exit /b 0
if not defined KEY exit /b 0
REM Quoted, so the twelve digits compare as text: as a number the key is too large for cmd.
if "%KEY%" GTR "%CHOSEN_KEY%" (set "CHOSEN=%CANDIDATE%" & set "CHOSEN_KEY=%KEY%")
if "%KEY%" GTR "%SWITCHER_KEY%" (set "SWITCHER=%CANDIDATE%" & set "SWITCHER_KEY=%KEY%")
exit /b 0

REM Sets CANDIDATE to %1 and, for a package folder here, KEY to the time in its name (yyyyMMddHHmm), WHOLE when it has
REM its files, CURRENT when it was made with this switch (package.txt, which devkit-deploy.bat writes last, matches its
REM files), and FAILED when a switch to it failed. The name is matched first, so it can only name a folder right here.
REM Folder dates are not used: copying the folders to another machine changes them.
:inspect
set "CANDIDATE=%~1"
set "KEY="
set "WHOLE="
set "CURRENT="
set "FAILED="
if /i "%CANDIDATE%"=="%SINGLE_PACKAGE%" goto :inspectFiles
set CANDIDATE | findstr /r /x /c:"CANDIDATE=%NAME_PATTERN%" >nul || exit /b 0
for /f "tokens=2-7 delims=_" %%A in ("%CANDIDATE%") do (
    set "DAY=%%A"
    set "MONTH=%%B"
    set "YEAR=%%C"
    set "HOUR=%%D"
    set "MINUTE=%%E"
    set "HALF=%%F"
)
REM The 1hh-100 form keeps 08 and 09 from being read as octal; 12 AM is hour 0, and 12 PM hour 12.
set /a "HOUR24=(1%HOUR%-100)%%12"
if /i "%HALF%"=="PM" set /a "HOUR24+=12"
set "HOUR24=0%HOUR24%"
set "KEY=%YEAR%%MONTH%%DAY%%HOUR24:~-2%%MINUTE%"

:inspectFiles
set "FOLDER=%RELEASE%\%CANDIDATE%"
if not exist "%FOLDER%\devkit-run.bat" exit /b 0
if not exist "%FOLDER%\app\DevKit.Web.exe" exit /b 0
if exist "%FOLDER%\switch-failed.txt" set "FAILED=1"
if not exist "%FOLDER%\package.txt" (
    set "WHOLE=1"
    exit /b 0
)
set "EXPECTED_FILES="
set "EXPECTED_BYTES="
for /f "usebackq tokens=1,2 delims==" %%A in ("%FOLDER%\package.txt") do (
    if /i "%%A"=="files" set "EXPECTED_FILES=%%B"
    if /i "%%A"=="bytes" set "EXPECTED_BYTES=%%B"
)
call :measure
if not "%FILES%"=="%EXPECTED_FILES%" exit /b 0
if not "%BYTES%"=="%EXPECTED_BYTES%" exit /b 0
set "WHOLE=1"
set "CURRENT=1"
exit /b 0

REM Sets FILES and BYTES to the count and total size of the files in FOLDER, but for the two the package itself writes,
REM as devkit-deploy.bat's :measure does for package.txt.
:measure
set "FILES=0"
set "BYTES=0"
for /r "%FOLDER%" %%F in (*) do if /i not "%%~nxF"=="package.txt" if /i not "%%~nxF"=="switch-failed.txt" set /a "FILES+=1, BYTES+=%%~zF"
exit /b 0

REM Once a package runs, keeps the newest KEEP_PACKAGES packages and removes the others, at about 110 MB each. Never one
REM a live state beside devkit.json names, as for another port, nor one anything still uses: a DevKit running from it, or
REM a keep-alive script waiting in it to start its DevKit again, as for another account. Such a folder cannot be renamed,
REM so it is renamed first, and removed only once that worked. DevKit_Previous has no date, so it is kept.
:removeOldPackages
REM What an earlier run could not remove whole, as a file in use was found only on the way.
for /d %%P in ("%RELEASE%\DevKit_*.removing") do rmdir /s /q "%%~fP" 2>nul
set "LIST=%TEMP%\devkit-packages-%RANDOM%%RANDOM%.txt"
(for /d %%P in ("%RELEASE%\DevKit_*") do call :listDated "%%~nxP") > "%LIST%"
REM Windows' own sort: one that Git puts first on the PATH reads /r as a file name.
for /f "skip=%KEEP_PACKAGES% tokens=2" %%N in ('%SystemRoot%\System32\sort.exe /r "%LIST%"') do call :removeOld "%%N"
del "%LIST%" >nul 2>&1
exit /b 0

:listDated
call :inspect "%~1"
if defined KEY echo %KEY% %CANDIDATE%
exit /b 0

:removeOld
set "OLD=%RELEASE%\%~1"
if /i "%~1"=="%CHOSEN%" exit /b 0
for %%S in ("%STATE_DIR%live-*.txt") do call :names "%%~fS" && exit /b 0
REM A link to another folder is left alone, so nothing outside this folder is ever deleted.
for %%P in ("%OLD%") do set "ATTRIBUTES=%%~aP"
if not "%ATTRIBUTES:l=%"=="%ATTRIBUTES%" exit /b 0
set "RETIRED=%~1.removing"
ren "%OLD%" "%RETIRED%" >nul 2>&1 || exit /b 0
rmdir /s /q "%RELEASE%\%RETIRED%"
call :log "Removed %~1, an old package: the %KEEP_PACKAGES% newest are kept"
exit /b 0

REM Exits 0 when live state %1 names OLD. What it names is compared through delayed expansion, so nothing it holds is
REM read as a command.
:names
setlocal EnableDelayedExpansion
set "NAMED="
set /p NAMED=<"%~1"
if /i "!NAMED!"=="!OLD!" (endlocal & exit /b 0)
endlocal
exit /b 1

REM Asks before switching to a package without package.txt. It was made before DevKit checked its packages, so whoever
REM saves a setting there drops what later packages added; or it was copied only in part. Exits 1 when the answer is no.
:confirmOlder
echo WARNING: %CHOSEN% has no package.txt, so its files cannot be checked. Either it was made before DevKit checked
echo its packages, and then whoever saves a setting while it runs loses those that later packages added, such as how
echo the Dashboard lays out the tools; or it was copied here only in part, and should be copied again instead.
if defined YES exit /b 0
if defined NO_PAUSE (
    echo ERROR: Pass --yes to go back to it all the same.
    exit /b 1
)
choice /c YN /m "Go back to it all the same"
if errorlevel 2 exit /b 1
exit /b 0

REM The single package's own scripts know nothing of switches. They are replaced by the switch's, which stop its DevKit
REM once another package runs; only when they differ, since cmd reads a running batch file line by line.
:refreshSingle
set "FROM=%RELEASE%\%SWITCH_WITH%"
set "TO=%RELEASE%\%SINGLE_PACKAGE%"
if not exist "%TO%\tools\" mkdir "%TO%\tools" || exit /b 1
for %%S in (devkit-run.bat tools\set-hosted-env.bat tools\devkit-startup.bat) do (
    fc /b "%FROM%\%%S" "%TO%\%%S" >nul 2>&1 || copy /y "%FROM%\%%S" "%TO%\%%S" >nul || exit /b 1
)
exit /b 0

REM An earlier devkit-deploy.bat made one package right in this folder, run by the devkit-run.bat here. That script
REM moves into DevKit_Previous before its DevKit is stopped: cmd reads a running batch file line by line, so it then
REM ends instead of starting that DevKit again. Its other files follow once that DevKit has stopped and let them go.
:retireSingleLoop
set "MOVED_SINGLE_LOOP="
if not exist "%RELEASE%\devkit-run.bat" exit /b 0
if not exist "%RELEASE%\%SINGLE_PACKAGE%\" mkdir "%RELEASE%\%SINGLE_PACKAGE%"
move /y "%RELEASE%\devkit-run.bat" "%RELEASE%\%SINGLE_PACKAGE%\" >nul && set "MOVED_SINGLE_LOOP=1"
exit /b 0

REM Puts that script back when nothing was stopped, so it still starts its DevKit again when that stops.
:restoreSingleLoop
if defined MOVED_SINGLE_LOOP move /y "%RELEASE%\%SINGLE_PACKAGE%\devkit-run.bat" "%RELEASE%\" >nul
exit /b 0

:retireSinglePackage
if not defined MOVED_SINGLE_LOOP exit /b 0
for %%E in (app tools README.md) do if exist "%RELEASE%\%%E" move /y "%RELEASE%\%%E" "%RELEASE%\%SINGLE_PACKAGE%\" >nul || call :log "WARNING: %RELEASE%\%%E could not be moved into %SINGLE_PACKAGE%; move it there by hand"
exit /b 0

REM Sets %1 to the process that listens on DevKit's port, empty when none does.
:listener
set "%~1="
for /f "tokens=5" %%P in ('netstat -ano ^| findstr /r /c:":%DEVKIT_PORT% .*LISTENING"') do set "%~1=%%P"
exit /b 0

REM Sets IMAGE to the program name of process %1.
:image
set "IMAGE=an unknown program"
for /f "tokens=1 delims=," %%I in ('tasklist /fi "PID eq %~1" /fo csv /nh') do set "IMAGE=%%~I"
exit /b 0

REM Prints %1 and adds it to devkit-run.log. Delayed expansion prints it as it is: a folder name with & or ) in it is
REM not read as a command. A log past 5 MB becomes devkit-run.1.log, replacing the one before, as devkit-run.bat does.
:log
set "LOG_LINE=[%date% %time:~0,8%] %~1"
if defined LOG if exist "%LOG%" for %%L in ("%LOG%") do if %%~zL GTR 5242880 move /y "%%~fL" "%%~dpnL.1%%~xL" >nul 2>&1
setlocal EnableDelayedExpansion
echo(!LOG_LINE!
if defined LOG >>"!LOG!" echo(!LOG_LINE!
endlocal
exit /b 0
