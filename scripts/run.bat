@echo off
echo Running DevKit...
call "%~dp0_set-sdk.bat" || (pause & exit /b 1)
cd /d "%~dp0..\DevKit.Web"
dotnet run --urls "http://localhost:5850"
echo.
echo App exited with code %ERRORLEVEL%
pause
