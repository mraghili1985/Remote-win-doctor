namespace RemoteWinDoctor.Services;

/// <summary>
/// Bootstraps the two one-time prerequisites needed before WinRM remoting works between
/// two machines (see AGENTS.md "Standard Workflow"). The target side can only ever be
/// scripted as a file the operator carries over and runs locally as Administrator --
/// WinRM isn't enabled there yet, so there is no remote channel to run it over.
/// The source (host) side has no such chicken-and-egg problem and can be applied
/// directly, in-process, since PowerShellExecutor runs local scripts without WinRM.
/// </summary>
public static class RemoteSetupHelper
{
    public static string GetSourceReadinessScript()
    {
        return @"
Write-Host 'STEP 1: Starting WinRM management service...' -ForegroundColor Yellow
Start-Service WinRM -ErrorAction SilentlyContinue
Set-Service WinRM -StartupType Automatic -ErrorAction SilentlyContinue
Write-Host '[OK] WinRM service is running.' -ForegroundColor Green

Write-Host 'STEP 2: Trusting local-network hosts for outgoing remote commands...' -ForegroundColor Yellow
Set-Item WSMan:\localhost\Client\TrustedHosts -Value '*' -Force
Write-Host '[OK] TrustedHosts set to *.' -ForegroundColor Green

Write-Host 'Source machine is ready to connect to remote targets.' -ForegroundColor Cyan
";
    }

    public static string GetTargetSetupScriptContent()
    {
        return @"@echo off
title RemoteWinDoctor - Target Machine Setup
echo ============================================================
echo   RemoteWinDoctor - Target Machine Remote-Access Setup
echo ============================================================
echo   Run this file as Administrator ONCE on the PC you want
echo   to manage remotely. It opens WinRM/PSRemoting so
echo   RemoteWinDoctor on another PC can connect to this one.
echo ============================================================
echo.

echo [1/2] Switching network profile to Private...
powershell -NoProfile -ExecutionPolicy Bypass -Command ""Set-NetConnectionProfile -NetworkCategory Private -ErrorAction SilentlyContinue""

echo [2/2] Enabling PowerShell Remoting (WinRM)...
powershell -NoProfile -ExecutionPolicy Bypass -Command ""Enable-PSRemoting -SkipNetworkProfileCheck -Force""

echo.
echo ============================================================
echo   This machine's local IPv4 address(es):
echo ============================================================
powershell -NoProfile -ExecutionPolicy Bypass -Command ""(Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -notmatch 'Loopback|vEthernet' }).IPAddress""

echo.
echo ============================================================
echo   Setup finished. Write down the IP above plus this
echo   Windows account's username/password, and enter them
echo   into RemoteWinDoctor on the other PC.
echo ============================================================
pause
";
    }
}
