@echo off
REM Makes the package that runs DevKit shared on another machine, in Release\ at the root of this repository:
REM   devkit-run.bat   starts DevKit shared, and starts it again whenever it stops with an error
REM   app\             DevKit, self-contained, so that machine needs no .NET install
REM   tools\           the checks devkit-run.bat shares with devkit-host.bat, and the script that writes its Startup file
REM   README.md        how to set that machine up ("Run it on another machine")
REM Copy the whole Release folder to that machine and run devkit-run.bat there. Release\ is made again from nothing
REM each time, so keep nothing else in it. It restores and cleans first, so the package always holds the code in this
REM folder. Settings, tokens and keys never go into it: DevKit keeps them per account on the machine it runs on.
REM Pass --no-pause to skip the closing pause when nobody is watching.
setlocal
for %%R in ("%~dp0..") do set "ROOT=%%~fR"
set "PROJECT=%ROOT%\DevKit.Web\DevKit.Web.csproj"
set "PACKAGE=%ROOT%\Release"
set "RUNTIME=win-x64"
set "RC=0"

REM Emptying a Release folder that is a junction or a symbolic link would empty the folder it points to.
if not exist "%PACKAGE%\" goto :unlinked
for %%P in ("%PACKAGE%") do set "ATTRIBUTES=%%~aP"
if "%ATTRIBUTES:l=%"=="%ATTRIBUTES%" goto :unlinked
echo ERROR: "%PACKAGE%" is a link to another folder. Remove the link, then run this again.
set "RC=1"
goto :done

:unlinked
call "%ROOT%\tools\set-sdk.bat"
if errorlevel 1 goto :failed

echo [1/4] Restoring...
dotnet restore "%PROJECT%" -r %RUNTIME% || goto :failed
echo [2/4] Cleaning...
dotnet clean "%PROJECT%" -c Release -r %RUNTIME% --nologo -v quiet || goto :failed
if not exist "%PACKAGE%" goto :emptied
REM Moved aside before it is deleted: the rename fails, and leaves the package whole, while a DevKit running from it
REM holds its files, where deleting would remove every file that DevKit does not hold open.
if exist "%PACKAGE%.old" rmdir /s /q "%PACKAGE%.old"
ren "%PACKAGE%" Release.old 2>nul
if errorlevel 1 goto :inuse
rmdir /s /q "%PACKAGE%.old"

:emptied
echo [3/4] Publishing...
dotnet publish "%PROJECT%" -c Release -r %RUNTIME% --self-contained --no-restore --nologo -o "%PACKAGE%\app" || goto :incomplete
echo [4/4] Adding the run script...
mkdir "%PACKAGE%\tools" || goto :incomplete
copy /y "%ROOT%\tools\set-hosted-env.bat" "%PACKAGE%\tools\" >nul || goto :incomplete
copy /y "%ROOT%\tools\devkit-startup.bat" "%PACKAGE%\tools\" >nul || goto :incomplete
copy /y "%ROOT%\README.md" "%PACKAGE%\" >nul || goto :incomplete
REM Last, so a folder with devkit-run.bat in it is always a whole package.
copy /y "%~dp0devkit-run.bat" "%PACKAGE%\" >nul || goto :incomplete
echo.
echo Package ready: "%PACKAGE%"
echo Copy that whole folder to the machine that runs DevKit, then run devkit-run.bat there.
goto :done

:inuse
echo ERROR: "%PACKAGE%" is in use, so it was left as it is. If DevKit is running from it, close that DevKit, then run
echo this again.
set "RC=1"
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
