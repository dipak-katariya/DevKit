@echo off
echo Cleaning DevKit...
call "%~dp0_set-sdk.bat" || (pause & exit /b 1)
cd /d "%~dp0..\DevKit.Web"
dotnet clean
echo.
pause
