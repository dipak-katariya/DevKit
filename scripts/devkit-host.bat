@echo off
REM Starts DevKit shared on this machine on port 5850: everyone opens the address it prints and signs in with their own
REM Windows account. It cleans, restores and builds the Release build first, so it always runs the code in this folder.
REM The TFS server, the session length and an https certificate come from this machine's devkit.json (see "Shared on
REM a server" in README.md).
REM Pass --no-pause to skip the closing pause when nobody is watching.
setlocal
set "ROOT=%~dp0.."
set "PROJECT=%ROOT%\DevKit.Web\DevKit.Web.csproj"
set "PORT=5850"
set "RC=0"

REM devkit.json sits beside the settings file, so DEVKIT_SETTINGS_PATH moves it too, as DevKit itself reads it.
set "CONFIG=%LOCALAPPDATA%\DevKit\devkit.json"
if defined DEVKIT_SETTINGS_PATH for %%F in ("%DEVKIT_SETTINGS_PATH%") do set "CONFIG=%%~dpFdevkit.json"
if exist "%CONFIG%" goto :configured
echo ERROR: "%CONFIG%" is missing. Shared DevKit reads its TFS server from it.
echo Create it as "Shared on a server" in README.md describes, then run this again.
set "RC=1"
goto :done

:configured
REM A running DevKit holds the port and locks the build output; stopping it is left to whoever started it.
netstat -ano | findstr /r /c:":%PORT% .*LISTENING" >nul
if errorlevel 1 goto :portfree
echo ERROR: Port %PORT% is already in use. Close the DevKit that is running, then run this again.
set "RC=1"
goto :done

:portfree
call "%ROOT%\tools\set-sdk.bat"
if errorlevel 1 goto :failed

echo [1/4] Cleaning...
dotnet clean "%PROJECT%" -c Release --nologo -v quiet || goto :failed
echo [2/4] Restoring...
dotnet restore "%PROJECT%" || goto :failed
echo [3/4] Building...
dotnet build "%PROJECT%" -c Release --no-restore --nologo || goto :failed

echo [4/4] Starting DevKit shared on port %PORT%...
REM Shared mode whatever devkit.json says, and Production: no Development error pages or settings.
set "DevKit__Mode=Hosted"
set "ASPNETCORE_ENVIRONMENT=Production"
REM An endpoint in devkit.json's "Kestrel" section (https with a certificate) replaces the plain http address.
set "URLS=--urls http://*:%PORT%"
findstr /i /c:"\"Kestrel\"" "%CONFIG%" >nul && set "URLS="
cd /d "%ROOT%\DevKit.Web"
dotnet run -c Release --no-build --no-launch-profile -- %URLS%
set "RC=%errorlevel%"
echo DevKit stopped with exit code %RC%.
goto :done

:failed
set "RC=%errorlevel%"
echo ERROR: DevKit was not started; the step above failed with exit code %RC%.

:done
echo.
if /i not "%~1"=="--no-pause" pause
exit /b %RC%
