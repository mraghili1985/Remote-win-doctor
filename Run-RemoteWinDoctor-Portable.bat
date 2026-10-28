@echo off
title RemoteWinDoctor Portable Pro Launcher
cd /d "%~dp0"

:: Auto-elevate to Administrator if required for system diagnostics and tuning
net session >nul 2>&1
if %errorLevel% neq 0 (
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

echo ============================================================
echo   RemoteWinDoctor Portable Pro (Native WPF .NET 10)
echo   Mode: Administrator Elevated (Full System Access)
echo ============================================================
echo   Launching standalone desktop application...
echo.
start "" "%~dp0Portable\RemoteWinDoctor.Desktop.exe"
