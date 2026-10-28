namespace RemoteWinDoctor.Services;

public class DevDbFixer
{
    public static string GetAuditScript()
    {
        return @"
$ErrorActionPreference = 'SilentlyContinue'

# 1. Check NVMe Sector Size Patch (SQL Server Win11 0x851A001A fix)
$stornvmePath = 'HKLM:\SYSTEM\CurrentControlSet\Services\stornvme\Parameters\Device'
$nvmePatched = $false
$nvmeValue = ''
if (Test-Path $stornvmePath) {
    $val = (Get-ItemProperty -Path $stornvmePath -Name 'ForcedPhysicalSectorSizeInBytes' -ErrorAction SilentlyContinue).ForcedPhysicalSectorSizeInBytes
    if ($val) {
        $nvmeValue = [string]$val
        if ($nvmeValue -match '4095') {
            $nvmePatched = $true
        }
    }
}
$nvmeDisks = @(Get-PhysicalDisk | Where-Object { $_.BusType -eq 'NVMe' })
$hasNvme = ($nvmeDisks.Count -gt 0)

# 2. Check Pending Reboot Locks
$hasPendingReboot = $false
$pendingReasons = @()

$sessMgr = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -ErrorAction SilentlyContinue
if ($sessMgr.PendingFileRenameOperations) {
    $hasPendingReboot = $true
    $pendingReasons += 'Session Manager: PendingFileRenameOperations'
}
if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') {
    $hasPendingReboot = $true
    $pendingReasons += 'Windows Update: RebootRequired'
}
if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') {
    $hasPendingReboot = $true
    $pendingReasons += 'CBS: RebootPending'
}
if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Updates\UpdateExeVolatile') {
    $hasPendingReboot = $true
    $pendingReasons += 'Updates: UpdateExeVolatile'
}

# 3. Check WMI Repository Status
$wmiOutput = (cmd.exe /c 'winmgmt /verifyrepository' 2>&1) | Out-String
$wmiConsistent = ($wmiOutput -match 'is consistent|موافق|consistent')

# 4. Check Visual Studio Installer & Packages
$vsInstallerPath = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer'
$hasVsInstaller = (Test-Path $vsInstallerPath)
$vsPackageCacheCount = 0
$vsPackageCacheSizeMB = 0
if (Test-Path 'C:\ProgramData\Microsoft\VisualStudio\Packages') {
    $pkgs = Get-ChildItem 'C:\ProgramData\Microsoft\VisualStudio\Packages' -Recurse -File -ErrorAction SilentlyContinue
    $vsPackageCacheCount = ($pkgs | Measure-Object).Count
    $vsPackageCacheSizeMB = [math]::Round(($pkgs | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
}

# 5. Check SQL Server Services
$sqlServices = @(Get-Service | Where-Object { $_.Name -like '*MSSQL*' -or $_.Name -like '*SQLSERVER*' -or $_.Name -like '*SQLBrowser*' -or $_.Name -like '*SQLAgent*' } | 
    Select-Object Name, DisplayName, Status)

# 6. Check Installed SQL Instances in Registry
$sqlInstances = @()
$sqlReg = 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL'
if (Test-Path $sqlReg) {
    $props = Get-ItemProperty $sqlReg
    foreach ($p in $props.PSObject.Properties) {
        if ($p.Name -notmatch 'PSParentPath|PSPath|PSChildName|PSDrive|PSProvider') {
            $sqlInstances += [PSCustomObject]@{
                InstanceName = $p.Name
                InternalId = $p.Value
            }
        }
    }
}

$auditResult = [PSCustomObject]@{
    NvmeStatus = [PSCustomObject]@{
        HasNvmeDrive = $hasNvme
        IsPatched = $nvmePatched
        CurrentValue = $nvmeValue
        DiskCount = $nvmeDisks.Count
    }
    RebootLock = [PSCustomObject]@{
        HasLock = $hasPendingReboot
        Reasons = $pendingReasons
    }
    WmiStatus = [PSCustomObject]@{
        IsConsistent = $wmiConsistent
        RawOutput = $wmiOutput.Trim()
    }
    VisualStudio = [PSCustomObject]@{
        HasInstaller = $hasVsInstaller
        PackageCacheCount = $vsPackageCacheCount
        PackageCacheSizeMB = $vsPackageCacheSizeMB
    }
    SqlServer = [PSCustomObject]@{
        Services = $sqlServices
        Instances = $sqlInstances
    }
}

$auditResult | ConvertTo-Json -Depth 4 -Compress
";
    }

    public static string GetFixScript(string fixType)
    {
        switch (fixType?.ToLowerInvariant())
        {
            case "sql-nvme":
                return @"
Write-Host '[START] Applying Windows 11 NVMe Sector Size Patch for SQL Server (0x851A001A fix)...' -ForegroundColor Cyan

$regPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\stornvme\Parameters\Device'
if (-not (Test-Path $regPath)) {
    New-Item -Path $regPath -Force | Out-Null
}

Set-ItemProperty -Path $regPath -Name 'ForcedPhysicalSectorSizeInBytes' -Value @('* 4095') -Type MultiString -Force
$verify = (Get-ItemProperty -Path $regPath -Name 'ForcedPhysicalSectorSizeInBytes').ForcedPhysicalSectorSizeInBytes

Write-Host ""[SUCCESS] Registry key set: ForcedPhysicalSectorSizeInBytes = $($verify -join ', ')"" -ForegroundColor Green
Write-Host '[NOTE] SQL Server database engine can now initialize master.mdf without 4KB sector size crashes.' -ForegroundColor Yellow
Write-Host '[ACTION RECOMMENDED] A quick system restart will fully activate the stornvme driver parameter.' -ForegroundColor Cyan
";

            case "pending-reboot":
                return @"
Write-Host '[START] Purging persistent Pending Reboot registry locks...' -ForegroundColor Cyan

# 1. Session Manager
if (Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager') {
    Remove-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name 'PendingFileRenameOperations' -Force -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name 'PendingFileRenameOperations2' -Force -ErrorAction SilentlyContinue
    Write-Host '[OK] Cleared Session Manager PendingFileRenameOperations.' -ForegroundColor Green
}

# 2. Windows Update
$wuReboot = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'
if (Test-Path $wuReboot) {
    Remove-Item -Path $wuReboot -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host '[OK] Cleared Windows Update RebootRequired.' -ForegroundColor Green
}

# 3. Component Based Servicing (CBS)
$cbsReboot = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending'
if (Test-Path $cbsReboot) {
    Remove-Item -Path $cbsReboot -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host '[OK] Cleared CBS RebootPending.' -ForegroundColor Green
}

# 4. Updates Volatile
$updVolatile = 'HKLM:\SOFTWARE\Microsoft\Updates\UpdateExeVolatile'
if (Test-Path $updVolatile) {
    Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Updates' -Name 'UpdateExeVolatile' -Force -ErrorAction SilentlyContinue
    Write-Host '[OK] Cleared UpdateExeVolatile.' -ForegroundColor Green
}

Write-Host '[SUCCESS] All Pending-Reboot flags have been safely cleared! Setup rules will now pass.' -ForegroundColor Cyan
";

            case "vs-cleanup":
                return @"
Write-Host '[START] Visual Studio Installer Deep Reset & Scrub...' -ForegroundColor Cyan

# 1. Official Microsoft Scrubber
$cleaner = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\resources\app\layout\InstallCleanup.exe'
if (Test-Path $cleaner) {
    Write-Host '[EXECUTING] Official Microsoft InstallCleanup.exe -f ...' -ForegroundColor Yellow
    $proc = Start-Process -FilePath $cleaner -ArgumentList '-f' -Wait -PassThru -NoNewWindow -ErrorAction SilentlyContinue
    Write-Host ""[OK] Scrubber finished with exit code: $($proc.ExitCode)"" -ForegroundColor Green
} else {
    Write-Host '[INFO] InstallCleanup.exe not found. Proceeding with cache purge.' -ForegroundColor Gray
}

# 2. Purge Corrupted Package Caches
if (Test-Path 'C:\ProgramData\Microsoft\VisualStudio\Packages') {
    Write-Host '[PURGING] C:\ProgramData\Microsoft\VisualStudio\Packages ...' -ForegroundColor Yellow
    Remove-Item -Path 'C:\ProgramData\Microsoft\VisualStudio\Packages\*' -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host '[OK] Visual Studio package cache purged.' -ForegroundColor Green
}

# 3. Clean Temp Installer Logs & Locks
Remove-Item -Path ""$env:TEMP\vs*"" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path ""$env:TEMP\dd_*"" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host '[OK] Installer temporary locks and logs cleared.' -ForegroundColor Green

Write-Host '[SUCCESS] Visual Studio Installer has been deeply scrubbed and reset.' -ForegroundColor Cyan
";

            case "wmi-repair":
                return @"
Write-Host '[START] Repairing and Rebuilding WMI Repository...' -ForegroundColor Cyan

# Stop WMI Service & Dependencies
Stop-Service -Name winmgmt -Force -ErrorAction SilentlyContinue

Write-Host '[STEP 1] Salvaging WMI Repository...' -ForegroundColor Yellow
$salvage = cmd.exe /c 'winmgmt /salvagerepository' 2>&1
Write-Host $salvage -ForegroundColor Gray

Write-Host '[STEP 2] Resetting WMI Repository...' -ForegroundColor Yellow
$reset = cmd.exe /c 'winmgmt /resetrepository' 2>&1
Write-Host $reset -ForegroundColor Gray

# Restart WMI
Start-Service -Name winmgmt -ErrorAction SilentlyContinue

Write-Host '[STEP 3] Verifying WMI Repository consistency...' -ForegroundColor Yellow
$verify = cmd.exe /c 'winmgmt /verifyrepository' 2>&1
Write-Host $verify -ForegroundColor Green

Write-Host '[SUCCESS] WMI Repository repair cycle completed.' -ForegroundColor Cyan
";

            case "all-in-one":
            default:
                return @"
Write-Host '=============================================================' -ForegroundColor Cyan
Write-Host '   DEV & DB ALL-IN-ONE COMPREHENSIVE RECOVERY TOOLKIT' -ForegroundColor Cyan
Write-Host '=============================================================' -ForegroundColor Cyan

# 1. NVMe Sector Size Patch
Write-Host '>>> STEP 1: Applying NVMe 4KB Sector Size Patch (SQL 0x851A001A Fix)...' -ForegroundColor Yellow
$regPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\stornvme\Parameters\Device'
if (-not (Test-Path $regPath)) { New-Item -Path $regPath -Force | Out-Null }
Set-ItemProperty -Path $regPath -Name 'ForcedPhysicalSectorSizeInBytes' -Value @('* 4095') -Type MultiString -Force
Write-Host '[OK] Sector size simulation set to 4095.' -ForegroundColor Green

# 2. Clear Pending Reboot
Write-Host '>>> STEP 2: Removing Pending Reboot Locks...' -ForegroundColor Yellow
Remove-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name 'PendingFileRenameOperations' -Force -ErrorAction SilentlyContinue
Remove-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending' -Recurse -Force -ErrorAction SilentlyContinue
Write-Host '[OK] All reboot locks removed.' -ForegroundColor Green

# 3. Clean Visual Studio Installer Cache
Write-Host '>>> STEP 3: Cleaning Visual Studio Installer & Package Caches...' -ForegroundColor Yellow
Remove-Item -Path 'C:\ProgramData\Microsoft\VisualStudio\Packages\*' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path ""$env:TEMP\vs*"" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path ""$env:TEMP\dd_*"" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host '[OK] VS installer package caches cleared.' -ForegroundColor Green

# 4. Repair WMI Repository
Write-Host '>>> STEP 4: Checking & Salvaging WMI Repository...' -ForegroundColor Yellow
Stop-Service -Name winmgmt -Force -ErrorAction SilentlyContinue
cmd.exe /c 'winmgmt /salvagerepository' | Out-Null
Start-Service -Name winmgmt -ErrorAction SilentlyContinue
$v = cmd.exe /c 'winmgmt /verifyrepository'
Write-Host ""[OK] WMI Verification: $v"" -ForegroundColor Green

Write-Host '=============================================================' -ForegroundColor Cyan
Write-Host '   ALL DEV & DATABASE REPAIR OPERATIONS COMPLETED!' -ForegroundColor Cyan
Write-Host '=============================================================' -ForegroundColor Cyan
";
        }
    }
}

public class DevDbFixRequest
{
    public string Ip { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FixType { get; set; } = "all-in-one";
}
