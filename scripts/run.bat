@echo off
echo Running DevKit...
cd /d "%~dp0..\DevKit.Web"
dotnet run --urls "http://localhost:5850"
echo.
echo App exited with code %ERRORLEVEL%
pause
