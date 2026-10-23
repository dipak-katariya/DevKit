@echo off
REM Starts DevKit shared on this machine on port 5850: everyone opens the address it prints and signs in with their own
REM Windows account. It cleans, restores and builds the Release build first, so it always runs the code in this folder.
REM The TFS server, the session length and an https certificate come from this machine's devkit.json (see "Shared on
REM a server" in README.md). To run DevKit on another machine without building it there, use devkit-deploy.bat.
REM Pass --no-pause to skip the closing pause when nobody is watching.
setlocal
set "ROOT=%~dp0.."
set "PROJECT=%ROOT%\DevKit.Web\DevKit.Web.csproj"
set "RC=0"

REM Before building: a running DevKit holds the port and locks the build output.
call "%ROOT%\tools\set-hosted-env.bat"
if errorlevel 1 goto :refused
call "%ROOT%\tools\set-sdk.bat"
if errorlevel 1 goto :failed

echo [1/4] Cleaning...
dotnet clean "%PROJECT%" -c Release --nologo -v quiet || goto :failed
echo [2/4] Restoring...
dotnet restore "%PROJECT%" || goto :failed
echo [3/4] Building...
dotnet build "%PROJECT%" -c Release --no-restore --nologo || goto :failed

echo [4/4] Starting DevKit shared on port %DEVKIT_PORT%...
cd /d "%ROOT%\DevKit.Web"
dotnet run -c Release --no-build --no-launch-profile -- %URLS%
set "RC=%errorlevel%"
echo DevKit stopped with exit code %RC%.
goto :done

:refused
set "RC=1"
goto :done

:failed
set "RC=%errorlevel%"
echo ERROR: DevKit was not started; the step above failed with exit code %RC%.

:done
echo.
if /i not "%~1"=="--no-pause" pause
exit /b %RC%
