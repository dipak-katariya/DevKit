@echo off
REM Puts DevKit-<port>.cmd in the Startup folder of the account that runs it, so DevKit on that port starts again by
REM itself each time that account signs in on this machine. tools\devkit-boot-task.bat has the same file run when the
REM machine starts too, before anyone signs in. devkit-run.bat calls this on every start, and devkit.bat for "devkit
REM autostart". Each port has a file of its own, so DevKits on two ports both start. Plain batch with no PowerShell:
REM endpoint protection such as Cortex XDR blocks a PowerShell script that writes to the Startup folder. The file is
REM written only when there is none yet or it would start DevKit differently, so nothing changes otherwise. What an
REM earlier DevKit put there for the same port, DevKit.cmd or DevKit.lnk, is removed, so DevKit starts once.
REM   %1  the port, kept in DEVKIT_PORT for the start at sign-in
REM   %2  the folder to start in
REM   %3  the program in it, and %4 its arguments, if any
REM   %5  the live state of a switch (LivePackage.cs), if any: then the package it names at sign-in is started, the one a
REM       switch made the DevKit that runs, and %2 only when it names none. So a failed switch, which names the package
REM       that ran before once more, never leaves the broken package to start at sign-in.
REM Exits 1, with a warning printed, when the file could not be put there.
setlocal
set "PORT=%~1"
set "FOLDER=%~2"
set "PROGRAM=%~3"
set "ARGUMENTS=%~4"
set "STATE=%~5"
set "STARTUP=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup"
set "LAUNCHER=%STARTUP%\DevKit-%PORT%.cmd"
set "MADE=%TEMP%\devkit-startup-%RANDOM%%RANDOM%.tmp"

REM The values are set while delayed expansion is off, so a ! in a folder name stays in it, and written with it on, so
REM characters such as & or ) are not read as commands. A % in a name is doubled, as DevKit-<port>.cmd reads it as a
REM percent sign so.
setlocal EnableDelayedExpansion
if "!FOLDER:~-1!"=="\" set "FOLDER=!FOLDER:~0,-1!"
set "FOLDER=!FOLDER:%%=%%%%!"
if defined STATE set "STATE=!STATE:%%=%%%%!"
> "%MADE%" (
    echo @echo off
    echo REM Starts DevKit on port !PORT! when !USERDOMAIN!\!USERNAME! signs in, and at boot when tools\devkit-boot-task.bat
    echo REM made a task for it. DevKit's scripts write this file; delete it to stop that.
    echo set "DEVKIT_PORT=!PORT!"
    echo set "FOLDER=!FOLDER!"
    echo set "PROGRAM=!PROGRAM!"
    echo set "ARGUMENTS=!ARGUMENTS!"
    if defined STATE echo set "STATE=!STATE!"
)
endlocal
REM The rest uses the values only through delayed expansion, so nothing the live state holds is read as a command. It
REM starts a package the live state names only when that package is right beside the one given, in the same Release
REM folder, which the switch checked; anything else the live state names is ignored. These lines are written with
REM delayed expansion off, so their ! stay, and %%%% becomes %%. The program is named with .\ because cmd skips the
REM current folder when NoDefaultCurrentDirectoryInExePath is set.
>>"%MADE%" echo setlocal EnableDelayedExpansion
if defined STATE >>"%MADE%" echo set "LIVE="
if defined STATE >>"%MADE%" echo if exist "!STATE!" set /p LIVE=^<"!STATE!"
if defined STATE >>"%MADE%" echo if defined LIVE for %%%%P in ("!LIVE!") do for %%%%R in ("!FOLDER!\..") do if /i "%%%%~dpP"=="%%%%~fR\" if exist "%%%%~fP\!PROGRAM!" set "FOLDER=%%%%~fP"
>>"%MADE%" echo cd /d "!FOLDER!"
>>"%MADE%" echo start "DevKit" /min cmd /c .\!PROGRAM! !ARGUMENTS!

fc /b "%MADE%" "%LAUNCHER%" >nul 2>&1
if not errorlevel 1 goto :current
move /y "%MADE%" "%LAUNCHER%" >nul 2>&1
if errorlevel 1 goto :failed
echo DevKit on port %PORT% now starts by itself when %USERDOMAIN%\%USERNAME% signs in on this machine ("%LAUNCHER%").
goto :olderFiles

:current
del "%MADE%" >nul 2>&1

:olderFiles
REM DevKit.cmd held one port for all; it goes only when it is this port's, so another port's start at sign-in stays.
if exist "%STARTUP%\DevKit.cmd" findstr /x /c:"set \"DEVKIT_PORT=%PORT%\"" "%STARTUP%\DevKit.cmd" >nul && del "%STARTUP%\DevKit.cmd" >nul 2>&1
if exist "%STARTUP%\DevKit.lnk" del "%STARTUP%\DevKit.lnk" >nul 2>&1
exit /b 0

:failed
del "%MADE%" >nul 2>&1
echo WARNING: DevKit-%PORT%.cmd could not be put in the Startup folder ("%STARTUP%").
echo DevKit starts now, but not by itself at the next sign-in.
exit /b 1
