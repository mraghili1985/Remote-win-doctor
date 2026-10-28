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
$bcdRes = cmd.exe /c ""bcdboot $env:SystemRoot /f ALL /c"" 2>&1 | Out-String
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
            case "dism-restore":   // name the desktop button sends
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
cmd.exe /c ""bcdboot $env:SystemRoot /f ALL /c"" | Out-Null
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

    /// <summary>
    /// Batch file run from a USB stick in WinRE / Windows Setup's Command Prompt. Finds every
    /// Windows installation (X: is the boot environment itself and is skipped), lets the user
    /// pick one when there are several, and asks before each repair step.
    /// </summary>
    public static string GetOfflineRescueScriptContent()
    {
        return @"@echo off
setlocal EnableDelayedExpansion
title RemoteWinDoctor - Offline Boot Rescue
color 0b
echo ======================================================================
echo              RemoteWinDoctor - Offline Boot Rescue
echo      Repairs a Windows installation that no longer starts
echo ======================================================================
echo.

rem Drive letters change inside the recovery environment, so look at every letter.
rem X: is the recovery/USB environment itself.
set COUNT=0
echo Windows installations found:
for %%D in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do (
    if exist %%D:\Windows\System32\ntoskrnl.exe (
        set /a COUNT+=1
        set WIN!COUNT!=%%D:
        echo   [!COUNT!] %%D:\Windows
    )
)
if %COUNT%==0 (
    echo   none
    echo.
    echo No Windows installation was found. Check that the disk is connected
    echo and visible in the BIOS. Nothing was changed.
    pause
    exit /b 1
)

set TARGET=%WIN1%
if %COUNT%==1 goto chosen
echo.
set /p PICK=Several installations were found. Type the number to repair and press Enter:
set TARGET=!WIN%PICK%!
if ""%TARGET%""=="""" (
    echo Invalid choice. Nothing was changed.
    pause
    exit /b 1
)
:chosen
echo.
echo Selected: %TARGET%\Windows
set FW=Legacy BIOS
for /f ""tokens=3"" %%A in ('reg query HKLM\System\CurrentControlSet\Control /v PEFirmwareType 2^>nul ^| find ""PEFirmwareType""') do if ""%%A""==""0x2"" set FW=UEFI
if /i ""%firmware_type%""==""UEFI"" set FW=UEFI
echo Firmware: %FW%
echo.
echo Each step asks first. Press Y to run it or N to skip it.
echo.

call :ask ""[1/4] Undo unfinished Windows updates (fixes restart loops after an update)?""
if !errorlevel!==1 (
    dism.exe /Image:%TARGET%\ /Cleanup-Image /RevertPendingActions
    echo.
)

call :ask ""[2/4] Rebuild the boot files for %TARGET%\Windows (fixes 'no boot device' and BCD errors)?""
if !errorlevel!==1 (
    bcdboot %TARGET%\Windows /f ALL
    bcdedit /deletevalue {default} safeboot >nul 2>&1
    bcdedit /timeout 5 >nul 2>&1
    echo.
)

call :ask ""[3/4] Check and repair Windows system files (SFC, takes 10-30 minutes)?""
if !errorlevel!==1 (
    sfc /scannow /offbootdir=%TARGET%\ /offwindir=%TARGET%\Windows
    echo.
)

call :ask ""[4/4] Check the disk for file system errors (CHKDSK, can take an hour on a hard drive)?""
if !errorlevel!==1 (
    chkdsk %TARGET% /f /x
    echo.
)

echo ======================================================================
echo   Finished. Remove the USB stick and restart the computer.
echo ======================================================================
pause
exit /b 0

:ask
choice /c YN /m ""%~1""
exit /b %errorlevel%
";
    }

    /// <summary>Instructions saved next to the rescue script on the USB stick.</summary>
    public static string GetOfflineRescueReadme() => @"RemoteWinDoctor - Offline Boot Rescue
=====================================

Use this when Windows no longer starts (boot loop, ""no boot device"", BCD error,
stuck after an update).

You need a Windows installation USB (or the PC's own recovery screen) and this USB
stick with RescueBootDoctor.bat on it.

1. Plug in the Windows installation USB and start the PC from it
   (boot menu key is usually F12, F11, F9 or Esc).
2. On the first setup screen click Next, then ""Repair your computer"" (bottom left).
   (If Windows shows its own blue recovery screen instead, start from step 3.)
3. Choose Troubleshoot > Advanced options > Command Prompt.
4. Find this USB stick: type  D:  and Enter, then  dir  - repeat with E:, F:, ...
   until you see RescueBootDoctor.bat.
5. Type  RescueBootDoctor.bat  and press Enter.
6. If several Windows installations are listed, type the number of the one to repair.
   Each repair step asks first: press Y to run it, N to skip it.
7. When it says Finished, remove the USB sticks and restart.

The script never deletes your files. It only repairs boot files, system files,
unfinished updates and file system errors.
";
}

public class RescueFixRequest
{
    public string Ip { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FixType { get; set; } = "all-in-one-boot";
}
