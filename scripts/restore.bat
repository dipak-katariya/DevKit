@echo off
echo Restoring DevKit packages...
call "%~dp0_set-sdk.bat" || (pause & exit /b 1)
cd /d "%~dp0..\DevKit.Web"
dotnet restore
echo.
pause
