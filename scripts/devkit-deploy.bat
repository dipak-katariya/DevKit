@echo off
REM Makes a new package of DevKit to run shared on a server, in a folder of its own in Release\ at the root of this
REM repository, named after the time it was made: DevKit_<day>_<month>_<year>_<hour>_<minute>_<AM|PM>, for example
REM Release\DevKit_29_10_2026_03_29_PM. It holds:
REM   app\             DevKit, self-contained, so the server needs no .NET install
REM   devkit-run.bat   keeps that DevKit running, and starts it again whenever it stops with an error
REM   tools\           the checks devkit-run.bat shares with devkit-host.bat, the script that writes its Startup file,
REM                    the one that has it start with the machine, and the one that locks a folder's permissions
REM   README.md        how to set the server up ("Run it on another machine")
REM   build.txt        when, by whom and from which commit it was made, and how many files were not committed then
REM   package.txt      written last, by the new DevKit.Web.exe: how many files the package has, their size, and the
REM                    SHA-256 of each, so a partial, damaged or changed copy is never run
REM It also puts "Run Latest DevKit.bat" in Release\, which makes the newest package the DevKit that runs, with a
REM maintenance page answering meanwhile, and removes old packages once the new one runs. A package that runs is never
REM touched, so DevKit stays up while this builds.
REM It runs the tests first, then restores and cleans, so the package always holds the code in this folder, and only
REM code whose tests pass. Release\ is left to this account, SYSTEM and administrators only (tools\devkit-lock.bat), as
REM DevKit runs its packages as this account. Settings, tokens and keys never go into a package: DevKit keeps them per
REM account on the machine it runs on.
REM Pass --no-pause to skip the closing pause when nobody is watching.
setlocal
for %%R in ("%~dp0..") do set "ROOT=%%~fR"
set "PROJECT=%ROOT%\DevKit.Web\DevKit.Web.csproj"
set "TESTS=%ROOT%\DevKit.Tests\DevKit.Tests.csproj"
set "RELEASE=%ROOT%\Release"
set "SWITCH=Run Latest DevKit.bat"
set "RUNTIME=win-x64"
set "NAME_PATTERN=DevKit_[0-9][0-9]_[0-9][0-9]_[0-9][0-9][0-9][0-9]_[0-9][0-9]_[0-9][0-9]_[AP]M"
set "RC=0"

REM A Release folder that is a junction or a symbolic link would put the package in the folder it points to.
if not exist "%RELEASE%\" goto :unlinked
for %%P in ("%RELEASE%") do set "ATTRIBUTES=%%~aP"
if "%ATTRIBUTES:l=%"=="%ATTRIBUTES%" goto :unlinked
echo ERROR: "%RELEASE%" is a link to another folder. Remove the link, then run this again.
set "RC=1"
goto :done

:unlinked
call :now
if errorlevel 1 goto :done
set "PACKAGE=%RELEASE%\DevKit_%STAMP%"
REM The switch runs the package with the latest time in its name, so a clock set back would leave this one unused.
call :newest
if "%NEWEST_KEY%" LSS "%KEY%" goto :named
echo ERROR: The clock reads %STAMP%, but "%NEWEST%" in "%RELEASE%" was made then or later, so the switch would not
echo take this package for the newest. Wait until the clock is past that time, or fix the clock, then run this again.
set "RC=1"
goto :done

:named
call "%ROOT%\tools\set-sdk.bat"
if errorlevel 1 goto :failed

echo [1/6] Testing...
dotnet test "%TESTS%" -c Release --nologo || goto :failed
if not exist "%RELEASE%\" mkdir "%RELEASE%" || goto :failed
call "%ROOT%\tools\devkit-lock.bat" "%RELEASE%" || goto :failed
REM Made at once, and only when it is not there: a second run in the same minute stops here instead of sharing it.
mkdir "%PACKAGE%" 2>nul && goto :claimed
echo ERROR: "%PACKAGE%" already exists: a package was made this minute. Wait a minute, then run this again.
set "RC=1"
goto :done

:claimed
call :revision
echo [2/6] Restoring...
dotnet restore "%PROJECT%" -r %RUNTIME% || goto :incomplete
echo [3/6] Cleaning...
dotnet clean "%PROJECT%" -c Release -r %RUNTIME% --nologo -v quiet || goto :incomplete
echo [4/6] Publishing into "%PACKAGE%"...
REM No .pdb: it would hand anyone who reads the package the paths and names of this machine's source files.
dotnet publish "%PROJECT%" -c Release -r %RUNTIME% --self-contained --no-restore --nologo -o "%PACKAGE%\app" -p:CopyOutputSymbolsToPublishDirectory=false -p:SourceRevisionId=%REVISION% || goto :incomplete
echo [5/6] Adding the run scripts...
mkdir "%PACKAGE%\tools" || goto :incomplete
for %%T in (set-hosted-env.bat devkit-startup.bat devkit-boot-task.bat devkit-lock.bat) do (
    copy /y "%ROOT%\tools\%%T" "%PACKAGE%\tools\" >nul || goto :incomplete
)
copy /y "%ROOT%\README.md" "%PACKAGE%\" >nul || goto :incomplete
copy /y "%~dp0devkit-run.bat" "%PACKAGE%\" >nul || goto :incomplete
> "%PACKAGE%\build.txt" (
    echo made=%STAMP%
    echo by=%USERDOMAIN%\%USERNAME% on %COMPUTERNAME%
    echo commit=%COMMIT%
    echo uncommitted=%UNCOMMITTED%
) || goto :incomplete
echo [6/6] Listing and hashing its files...
REM Last into the package: "%SWITCH%" takes it for a whole package only when its files still match.
"%PACKAGE%\app\DevKit.Web.exe" --write-manifest "%PACKAGE%" || goto :incomplete
REM Once the package is whole, and only when it changed: cmd reads a batch file line by line as it runs, so a switch
REM running right now would go on in the middle of the new text.
fc /b "%~dp0devkit-run-latest.bat" "%RELEASE%\%SWITCH%" >nul 2>&1 || copy /y "%~dp0devkit-run-latest.bat" "%RELEASE%\%SWITCH%" >nul || goto :incomplete
call :retireOldSwitch
echo.
if "%COMMIT%"=="unknown" echo WARNING: git could not say which commit this folder is on, so build.txt in the package cannot either.
if not "%COMMIT%"=="unknown" if not "%UNCOMMITTED%"=="0" echo WARNING: %UNCOMMITTED% files were not committed when it was made; build.txt in the package says so.
echo Package ready: "%PACKAGE%"
echo To make it the DevKit that runs, run "%SWITCH%" in "%RELEASE%". When DevKit runs on another machine, copy
echo the new package folder and "%SWITCH%" into its DevKit folder there first, run tools\devkit-lock.bat on that
echo folder once, and run "%SWITCH%" there.
goto :done

:incomplete
set "RC=%errorlevel%"
REM A half-made package must not reach a server by mistake.
rmdir /s /q "%PACKAGE%"
goto :report

:failed
set "RC=%errorlevel%"

:report
echo ERROR: No package was made; the step above failed with exit code %RC%.

:done
echo.
if /i not "%~1"=="--no-pause" pause
exit /b %RC%

REM Sets COMMIT to the commit this folder is on, UNCOMMITTED to how many files differ from it, and REVISION to what the
REM program's version names: the commit, marked when files were not committed, so the version never claims code it lacks.
:revision
set "COMMIT=unknown"
set "UNCOMMITTED=unknown"
for /f %%C in ('git -C "%ROOT%" rev-parse HEAD 2^>nul') do set "COMMIT=%%C"
REM Windows' own find: the one Git puts first on the PATH would search the whole drive.
if not "%COMMIT%"=="unknown" for /f %%N in ('git -C "%ROOT%" status --porcelain 2^>nul ^| %SystemRoot%\System32\find.exe /c /v ""') do set "UNCOMMITTED=%%N"
set "REVISION=%COMMIT%"
if not "%COMMIT%"=="unknown" if not "%UNCOMMITTED%"=="0" set "REVISION=%COMMIT%.uncommitted"
exit /b 0

REM devkit-run-latest.bat is what an earlier devkit-deploy.bat named "%SWITCH%" in Release\. It is removed, so only
REM the current one is there to run.
:retireOldSwitch
set "OLD_SWITCH=%RELEASE%\devkit-run-latest.bat"
if not exist "%OLD_SWITCH%" exit /b 0
del "%OLD_SWITCH%" >nul 2>&1
if not exist "%OLD_SWITCH%" echo Removed devkit-run-latest.bat, the earlier name of "%SWITCH%".
if exist "%OLD_SWITCH%" echo WARNING: "%OLD_SWITCH%" could not be removed. Delete it, and run "%SWITCH%" only.
exit /b 0

REM Sets NEWEST and NEWEST_KEY to the package in Release\ with the latest time in its name (yyyyMMddHHmm, as "%SWITCH%"
REM reads it), both empty when there is none.
:newest
set "NEWEST="
set "NEWEST_KEY="
if not exist "%RELEASE%\" exit /b 0
for /d %%P in ("%RELEASE%\DevKit_*") do call :consider "%%~nxP"
exit /b 0

:consider
set "CANDIDATE=%~1"
set CANDIDATE | findstr /r /x /c:"CANDIDATE=%NAME_PATTERN%" >nul || exit /b 0
for /f "tokens=2-7 delims=_" %%A in ("%CANDIDATE%") do (
    set "C_DAY=%%A"
    set "C_MONTH=%%B"
    set "C_YEAR=%%C"
    set "C_HOUR=%%D"
    set "C_MINUTE=%%E"
    set "C_HALF=%%F"
)
REM The 1hh-100 form keeps 08 and 09 from being read as octal; 12 AM is hour 0, and 12 PM hour 12.
set /a "C_HOUR24=(1%C_HOUR%-100)%%12"
if /i "%C_HALF%"=="PM" set /a "C_HOUR24+=12"
set "C_HOUR24=0%C_HOUR24%"
set "C_KEY=%C_YEAR%%C_MONTH%%C_DAY%%C_HOUR24:~-2%%C_MINUTE%"
REM Quoted, so the twelve digits compare as text: as a number the key is too large for cmd.
if "%C_KEY%" GTR "%NEWEST_KEY%" (set "NEWEST=%CANDIDATE%" & set "NEWEST_KEY=%C_KEY%")
exit /b 0

REM Sets STAMP to this minute as DD_MM_YYYY_hh_mm_AM or PM, and KEY to it as yyyyMMddHHmm. robocopy prints the time as
REM YYYY/MM/DD HH:MM:SS whatever the regional settings say; it is given a source that cannot exist, "|", so it copies
REM nothing.
:now
set "MINUTE="
for /f "tokens=1-5 delims=/: " %%A in ('robocopy "|" . /njh ^| findstr /c:":"') do (
    set "YEAR=%%A"
    set "MONTH=%%B"
    set "DAY=%%C"
    set "HOUR=%%D"
    set "MINUTE=%%E"
    goto :clock
)

:clock
if defined MINUTE goto :twelveHour
echo ERROR: The time could not be read, so the package could not be named.
set "RC=1"
exit /b 1

:twelveHour
REM The 1HH-100 form keeps 08 and 09 from being read as octal; hour 0 is 12 AM, and hour 12 is 12 PM.
set /a "HOUR12=(1%HOUR%-100+11)%%12+1"
set "HALF=AM"
if 1%HOUR% GEQ 112 set "HALF=PM"
set "HOUR12=0%HOUR12%"
set "STAMP=%DAY%_%MONTH%_%YEAR%_%HOUR12:~-2%_%MINUTE%_%HALF%"
set "KEY=%YEAR%%MONTH%%DAY%%HOUR%%MINUTE%"
exit /b 0
