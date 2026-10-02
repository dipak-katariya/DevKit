@echo off
REM ---------------------------------------------------------------------------
REM Pin MSBuild to whichever SDK the dotnet CLI selects for this repo.
REM `dotnet --version` is run from the folder that owns global.json - DevKit.Web,
REM not the repo root - so global.json (including its rollForward rule) decides
REM the version; we only look up that version's install path. Neither the SDK
REM version nor its install location is ever hardcoded.
REM Pass a directory as %1 to resolve against a different global.json.
REM ---------------------------------------------------------------------------
set "SDK_DIR=%~1"
if not defined SDK_DIR set "SDK_DIR=%~dp0..\DevKit.Web"

set "SDK_TMP=%TEMP%\_devkit_sdk_%RANDOM%.txt"
pushd "%SDK_DIR%" 2>nul
if errorlevel 1 (
    echo ERROR: Cannot enter "%SDK_DIR%" - expected the folder containing global.json.
    exit /b 1
)
dotnet --version >"%SDK_TMP%" 2>nul
set "DOTNET_RC=%errorlevel%"
popd

REM A non-zero exit means no installed SDK satisfies global.json; the output is
REM then a diagnostic listing, not a version, so only trust it on success.
set "SDK_VER="
if "%DOTNET_RC%"=="0" set /p SDK_VER=<"%SDK_TMP%"
del "%SDK_TMP%" >nul 2>&1

if not defined SDK_VER (
    echo ERROR: No installed .NET SDK satisfies %SDK_DIR%\global.json.
    echo Install the matching SDK from https://dot.net/download, or edit global.json.
    dotnet --list-sdks
    exit /b 1
)

set "SDK_PATH="
for /f "tokens=1,* delims= " %%a in ('dotnet --list-sdks 2^>nul') do (
    if "%%a"=="%SDK_VER%" set "SDK_PATH=%%b"
)

if not defined SDK_PATH (
    echo ERROR: SDK %SDK_VER% was selected but is not listed by dotnet --list-sdks.
    exit /b 1
)

REM SDK_PATH is wrapped in brackets, e.g. [C:\Program Files\dotnet\sdk] - strip them.
set "SDK_PATH=%SDK_PATH:~1,-1%"
set "MSBuildSDKsPath=%SDK_PATH%\%SDK_VER%\Sdks"
echo Using .NET SDK %SDK_VER%
exit /b 0
