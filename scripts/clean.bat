@echo off
echo Cleaning DevKit...
cd /d "%~dp0..\DevKit.Web"
dotnet clean
echo.
pause
