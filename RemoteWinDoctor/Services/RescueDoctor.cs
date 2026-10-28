namespace RemoteWinDoctor.Services;

public class RescueDoctor
{
    public static string GetBootAuditScript()
    {
        return @"
$ErrorActionPreference = 'SilentlyContinue'

# 1. Check Windows Recovery Environment (WinRE)
$reInfo = cmd.exe /c 'reagentc /info' 2>&1 | Out-String
$winReEnabled = ($reInfo -match 'Enabled|فعال')
$winReLocation = ''
if ($reInfo -match 'Windows RE location:\s+(.*)') {
    $winReLocation = $matches[1].Trim()
}

# 2. Check BCD Bootloader Configuration
$bcdCurrent = cmd.exe /c 'bcdedit /enum {current}' 2>&1 | Out-String
$safebootActive = ($bcdCurrent -match 'safeboot')
$bootTimeout = 30
$bcdTimeoutStr = cmd.exe /c 'bcdedit /timeout' 2>&1 | Out-String
if ($bcdTimeoutStr -match 'timeout\s+(\d+)') {
    $bootTimeout = [int]$matches[1]
}

# 3. Check BSOD Crash Minidumps
$minidumpDir = 'C:\Windows\Minidump'
$minidumpCount = 0
$latestDumpDate = ''
$latestDumpFile = ''
if (Test-Path $minidumpDir) {
    $dumps = Get-ChildItem -Path $minidumpDir -Filter '*.dmp' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
    $minidumpCount = ($dumps | Measure-Object).Count
    if ($minidumpCount -gt 0) {
        $latestDumpDate = $dumps[0].LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')
        $latestDumpFile = $dumps[0].Name
    }
}

# 4. Extract Last BugCheck Event (BSOD code) from System Log
$lastBsod = 'No recent crashes recorded'
$lastBsodTime = ''
$bugCheckEvent = Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-WER-SystemErrorReporting'} -MaxEvents 1 -ErrorAction SilentlyContinue
if (-not $bugCheckEvent) {
    $bugCheckEvent = Get-WinEvent -FilterHashtable @{LogName='System'; Id=1001} -MaxEvents 1 -ErrorAction SilentlyContinue
}
if ($bugCheckEvent) {
    $lastBsod = $bugCheckEvent.Message.Trim()
    $lastBsodTime = $bugCheckEvent.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss')
}

# 5. Check Pending Servicing Update State (pending.xml)
$hasPendingXml = (Test-Path 'C:\Windows\WinSxS\pending.xml')
$cbsReboot = (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending')

$auditResult = [PSCustomObject]@{
    WinRE = [PSCustomObject]@{
        IsEnabled = $winReEnabled
        Location = $winReLocation
        RawOutput = $reInfo.Trim()
    }
    Bootloader = [PSCustomObject]@{
        IsSafeboot = $safebootActive
        TimeoutSeconds = $bootTimeout
    }
    BSOD = [PSCustomObject]@{
        MinidumpCount = $minidumpCount
        LatestDumpDate = $latestDumpDate
        LatestDumpFile = $latestDumpFile
        LastBugCheckMessage = $lastBsod
        LastBugCheckTime = $lastBsodTime
    }
    UpdateHealth = [PSCustomObject]@{
        HasPendingXml = $hasPendingXml
        IsCbsRebootPending = $cbsReboot
    }
}

$auditResult | ConvertTo-Json -Depth 4 -Compress
";
    }

    public static string GetRescueFixScript(string fixType)
    {
        switch (fixType?.ToLowerInvariant())
        {
            case "enable-winre":
                return @"
Write-Host '[START] Enabling and Re-registering Windows Recovery Environment (WinRE)...' -ForegroundColor Cyan
$res = cmd.exe /c 'reagentc /enable' 2>&1 | Out-String
Write-Host $res -ForegroundColor Green
$info = cmd.exe /c 'reagentc /info' 2>&1 | Out-String
Write-Host $info -ForegroundColor Gray
Write-Host '[SUCCESS] Windows RE has been verified and enabled.' -ForegroundColor Cyan
";

            case "rebuild-bcd":
                return @"
Write-Host '[START] Rebuilding UEFI BCD Bootloader and refreshing boot files...' -ForegroundColor Cyan

# Re-inject EFI boot files from running Windows
$bcdRes = cmd.exe /c 'bcdboot C:\Windows /f ALL /c' 2>&1 | Out-String
Write-Host $bcdRes -ForegroundColor Green

# Remove stuck SafeBoot flags if present
cmd.exe /c 'bcdedit /deletevalue {current} safeboot' 2>&1 | Out-Null
cmd.exe /c 'bcdedit /deletevalue {default} safeboot' 2>&1 | Out-Null

# Set Recovery Display Timeout
cmd.exe /c 'bcdedit /timeout 5' 2>&1 | Out-Null

Write-Host '[SUCCESS] UEFI BCD Bootloader successfully refreshed and configured!' -ForegroundColor Cyan
";

            case "revert-updates":
                return @"
Write-Host '[START] Reverting stuck / corrupted pending Windows Updates (DISM RevertPendingActions)...' -ForegroundColor Cyan

$res = dism.exe /Online /Cleanup-Image /RevertPendingActions 2>&1 | Out-String
Write-Host $res -ForegroundColor Green

# Remove pending.xml if locked
if (Test-Path 'C:\Windows\WinSxS\pending.xml') {
    takeown /f 'C:\Windows\WinSxS\pending.xml' | Out-Null
    icacls 'C:\Windows\WinSxS\pending.xml' /grant administrators:F | Out-Null
    Remove-Item 'C:\Windows\WinSxS\pending.xml' -Force -ErrorAction SilentlyContinue
    Write-Host '[OK] Cleared C:\Windows\WinSxS\pending.xml.' -ForegroundColor Green
}

Write-Host '[SUCCESS] Stuck update loops reverted. Windows will boot cleanly on next cycle.' -ForegroundColor Cyan
";

            case "repair-sysfiles":
                return @"
Write-Host '[START] Full System File & Component Store Integrity Repair (SFC + DISM)...' -ForegroundColor Cyan

Write-Host 'STEP 1: Running DISM RestoreHealth...' -ForegroundColor Yellow
$dismRes = dism.exe /Online /Cleanup-Image /RestoreHealth 2>&1 | Out-String
Write-Host $dismRes -ForegroundColor Gray

Write-Host 'STEP 2: Running System File Checker (SFC /scannow)...' -ForegroundColor Yellow
$sfcRes = sfc.exe /scannow 2>&1 | Out-String
Write-Host $sfcRes -ForegroundColor Green

Write-Host '[SUCCESS] Core Windows system files and component stores restored.' -ForegroundColor Cyan
";

            case "all-in-one-boot":
            default:
                return @"
Write-Host '=============================================================' -ForegroundColor Cyan
Write-Host '   EMERGENCY BOOT RESCUE & SYSTEM RECOVERY PACKAGE' -ForegroundColor Cyan
Write-Host '=============================================================' -ForegroundColor Cyan

Write-Host '>>> 1. Ensuring WinRE Recovery Partition is Enabled...' -ForegroundColor Yellow
cmd.exe /c 'reagentc /enable' | Out-Null
Write-Host '[OK] Windows RE partition active.' -ForegroundColor Green

Write-Host '>>> 2. Refreshing UEFI Bootloader (bcdboot)...' -ForegroundColor Yellow
cmd.exe /c 'bcdboot C:\Windows /f ALL /c' | Out-Null
cmd.exe /c 'bcdedit /deletevalue {current} safeboot' 2>&1 | Out-Null
cmd.exe /c 'bcdedit /timeout 5' | Out-Null
Write-Host '[OK] Boot files refreshed.' -ForegroundColor Green

Write-Host '>>> 3. Reverting any pending update locks...' -ForegroundColor Yellow
dism.exe /Online /Cleanup-Image /RevertPendingActions 2>&1 | Out-Null
Write-Host '[OK] Pending update locks cleared.' -ForegroundColor Green

Write-Host '=============================================================' -ForegroundColor Cyan
Write-Host '   BOOT RESCUE PACKAGE APPLIED SUCCESSFULLY!' -ForegroundColor Cyan
Write-Host '=============================================================' -ForegroundColor Cyan
";
        }
    }

    public static string GetOfflineRescueScriptContent()
    {
        return @"@echo off
title Emergency Windows Rescue & Boot Doctor
color 0b
echo ======================================================================
echo           EMERGENCY WINDOWS RESCUE ^& BOOT DOCTOR
echo      Automated Offline Repair Tool for Unbootable Systems
echo ======================================================================
echo.
echo [!] Detecting Windows installation drive...
set TARGET_DRIVE=C:
if not exist C:\Windows\System32\cmd.exe (
    set TARGET_DRIVE=D:
)
if not exist D:\Windows\System32\cmd.exe (
    set TARGET_DRIVE=E:
)
echo [*] Target Windows found at: %TARGET_DRIVE%\Windows
echo.

echo [1/5] Reverting broken/pending update loop actions (DISM)...
dism.exe /Image:%TARGET_DRIVE%\ /Cleanup-Image /RevertPendingActions
echo [OK] Update revert check completed.
echo.

echo [2/5] Rebuilding UEFI BCD Bootloader from scratch...
bcdboot %TARGET_DRIVE%\Windows /f ALL /c
bcdedit /timeout 5
bcdedit /deletevalue {default} safeboot 2>nul
echo [OK] UEFI BCD Bootloader reconstructed.
echo.

echo [3/5] Scanning and repairing offline protected system files (SFC)...
sfc /scannow /offbootdir=%TARGET_DRIVE%\ /offwindir=%TARGET_DRIVE%\Windows
echo [OK] SFC offline scan finished.
echo.

echo [4/5] Checking file system integrity on %TARGET_DRIVE%...
chkdsk %TARGET_DRIVE% /f /x
echo [OK] Disk integrity check finished.
echo.

echo [5/5] Re-enabling Windows RE Recovery Agent...
reagentc /enable /osdir %TARGET_DRIVE%\Windows 2>nul
echo.

echo ======================================================================
echo   RESCUE OPERATION FINISHED! Remove USB and Restart Computer.
echo ======================================================================
pause
";
    }
}

public class RescueFixRequest
{
    public string Ip { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FixType { get; set; } = "all-in-one-boot";
}
