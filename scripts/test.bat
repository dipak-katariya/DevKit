@echo off
echo Running DevKit tests...
call "%~dp0_set-sdk.bat" || (pause & exit /b 1)
cd /d "%~dp0..\DevKit.Tests"
dotnet test
echo.
pause
