@echo off
REM Puts DevKit.cmd in the Startup folder of the account that runs it, so DevKit starts again by itself each time that
REM account signs in on this machine, after a restart too. devkit-run.bat calls it on every start, and devkit.bat for
REM "devkit autostart". Plain batch with no PowerShell: endpoint protection such as Cortex XDR blocks a PowerShell
REM script that writes to the Startup folder. The file is written only when there is none yet or it starts DevKit from
REM another folder, program or port, so a package that was moved or replaced takes over, and nothing changes otherwise.
REM A DevKit.lnk an earlier DevKit put there is removed, so DevKit starts once.
REM   %1  the port, kept in DEVKIT_PORT for the start at sign-in
REM   %2  the folder to start in
REM   %3  the program in it, and %4 its arguments, if any
REM Exits 1, with a warning printed, when the file could not be put there.
setlocal EnableDelayedExpansion
set "PORT=%~1"
set "FOLDER=%~2"
if "!FOLDER:~-1!"=="\" set "FOLDER=!FOLDER:~0,-1!"
set "PROGRAM=%~3"
set "ARGUMENTS=%~4"
set "STARTUP=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup"
set "LAUNCHER=%STARTUP%\DevKit.cmd"
set "MADE=%TEMP%\devkit-startup-%RANDOM%%RANDOM%.tmp"

REM Delayed expansion keeps characters such as & or ) in the folder from being read as commands. The program is named
REM with .\ because cmd skips the current folder when NoDefaultCurrentDirectoryInExePath is set.
> "%MADE%" (
    echo @echo off
    echo REM Starts DevKit when !USERDOMAIN!\!USERNAME! signs in. DevKit's scripts write this file; delete it to stop that.
    echo set "DEVKIT_PORT=!PORT!"
    echo cd /d "!FOLDER!"
    echo start "DevKit" /min cmd /c .\!PROGRAM! !ARGUMENTS!
)

fc /b "%MADE%" "%LAUNCHER%" >nul 2>&1
if not errorlevel 1 goto :current
move /y "%MADE%" "%LAUNCHER%" >nul 2>&1
if errorlevel 1 goto :failed
echo DevKit now starts by itself when %USERDOMAIN%\%USERNAME% signs in on this machine ("%LAUNCHER%").
goto :olderShortcut

:current
del "%MADE%" >nul 2>&1

:olderShortcut
if exist "%STARTUP%\DevKit.lnk" del "%STARTUP%\DevKit.lnk" >nul 2>&1
exit /b 0

:failed
del "%MADE%" >nul 2>&1
echo WARNING: DevKit.cmd could not be put in the Startup folder ("%STARTUP%").
echo DevKit starts now, but not by itself at the next sign-in.
exit /b 1
