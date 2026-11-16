@echo off
REM Leaves a folder, and everything in it, to the account that runs this, SYSTEM and administrators only. DevKit runs
REM its packages as that account, so anyone else who may change them, or the Release folder they are in, could run their
REM own code as it; on a new drive Windows lets every signed-in user change what is made there. devkit-deploy.bat locks
REM the Release folder it makes packages in, and the switch refuses a package anyone else may change. Run it by hand on
REM the DevKit folder of another machine after copying packages there, and on the repository folder itself:
REM   tools\devkit-lock.bat "D:\DevKit\Release"
REM It runs no PowerShell. To undo it, give the folder its usual permissions back with
REM   icacls "<folder>" /inheritance:e
REM Exits 1, with the reason printed, when the folder is missing or its permissions could not be set.
setlocal
set "TARGET=%~f1"
if "%~1"=="" goto :usage
if exist "%TARGET%\" goto :lock
echo ERROR: "%TARGET%" is not a folder.
exit /b 1

:usage
echo Usage: devkit-lock.bat "<folder>"
exit /b 1

:lock
REM First every permission given to the folder itself goes, so it has only what the one above passes on; then it stops
REM taking those too, and keeps only full control for SYSTEM, administrators (by their well-known SIDs, the same in
REM every language) and this account, passed on to everything in it.
icacls "%TARGET%" /reset /q >nul
if errorlevel 1 goto :failed
icacls "%TARGET%" /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "%USERDOMAIN%\%USERNAME%:(OI)(CI)F" /q >nul
if errorlevel 1 goto :failed
REM What is in it already drops any permission of its own, so it has only what the folder passes on.
dir /b /a "%TARGET%" 2>nul | findstr /r "." >nul || goto :locked
icacls "%TARGET%\*" /reset /t /c /q >nul
if errorlevel 1 goto :failed

:locked
echo Only %USERDOMAIN%\%USERNAME%, SYSTEM and administrators may change "%TARGET%" now.
exit /b 0

:failed
echo ERROR: The permissions of "%TARGET%" could not be set. Run this as its owner, or as an administrator.
exit /b 1
