@echo off
title RemoteWinDoctor Launcher
cd /d "%~dp0RemoteWinDoctor"
echo ============================================================
echo   RemoteWinDoctor - AI & Windows Remote Diagnostics Pro
echo ============================================================
echo   Starting server on http://localhost:5055 ...
echo.
dotnet run --launch-profile ""
pause
