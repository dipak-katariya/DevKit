@echo off
echo Building DevKit...
cd /d "%~dp0..\DevKit.Web"
dotnet build
echo.
pause
