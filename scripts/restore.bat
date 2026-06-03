@echo off
echo Restoring DevKit packages...
cd /d "%~dp0..\DevKit.Web"
dotnet restore
echo.
pause
