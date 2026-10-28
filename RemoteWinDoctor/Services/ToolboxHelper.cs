namespace RemoteWinDoctor.Services;

/// <summary>
/// One-shot diagnostic/action scripts for the System Toolbox tab. Each method returns a
/// self-contained PowerShell script that writes human-readable status lines via Write-Host,
/// matching the convention used by DevDbFixer/RescueDoctor so output can be dumped straight
/// into a log panel without extra parsing.
/// </summary>
public static class ToolboxHelper
{
    public static string GetResourceSnapshotScript()
    {
        return @"
$cpu = (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
$os = Get-CimInstance Win32_OperatingSystem
$ramTotalGB = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
$ramFreeGB = [math]::Round($os.FreePhysicalMemory / 1MB, 1)
$ramUsedPct = [math]::Round((($os.TotalVisibleMemorySize - $os.FreePhysicalMemory) / $os.TotalVisibleMemorySize) * 100, 1)
Write-Host ""CPU Load: $cpu%""
Write-Host ""RAM Used: $ramUsedPct% ($($ramTotalGB - $ramFreeGB) GB of $ramTotalGB GB)""

Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object {
    $freeGB = [math]::Round($_.FreeSpace / 1GB, 1)
    $totalGB = [math]::Round($_.Size / 1GB, 1)
    $usedPct = if ($totalGB -gt 0) { [math]::Round((($totalGB - $freeGB) / $totalGB) * 100, 1) } else { 0 }
    Write-Host ""Disk $($_.DeviceID) Used: $usedPct% ($($totalGB - $freeGB) GB of $totalGB GB, $freeGB GB free)""
}
";
    }

    public static string GetBatteryHealthScript()
    {
        return @"
$batteries = Get-CimInstance Win32_Battery
if (-not $batteries) {
    Write-Host 'No battery detected (desktop PC or battery removed).'
    return
}
foreach ($b in $batteries) {
    Write-Host ""Battery: $($b.Name)""
    Write-Host ""Charge Remaining: $($b.EstimatedChargeRemaining)%""
}
try {
    $design = (Get-CimInstance -Namespace root\wmi -ClassName BatteryStaticData -ErrorAction Stop).DesignedCapacity
    $full = (Get-CimInstance -Namespace root\wmi -ClassName BatteryFullChargedCapacity -ErrorAction Stop).FullChargedCapacity
    if ($design -and $full) {
        $wearPct = [math]::Round((($design - $full) / $design) * 100, 1)
        $healthPct = 100 - $wearPct
        Write-Host ""Designed Capacity: $design mWh | Current Full Charge: $full mWh""
        Write-Host ""Battery Health: $healthPct% (Wear: $wearPct%)""
        if ($wearPct -gt 30) { Write-Host '[WARNING] Battery has significant wear - consider replacement.' }
    }
} catch {
    Write-Host 'Detailed wear data not available on this device (vendor-specific WMI class missing).'
}
";
    }

    public static string GetDiskHealthScript()
    {
        return @"
Get-PhysicalDisk | ForEach-Object {
    $disk = $_
    Write-Host ""Disk: $($disk.FriendlyName) | Media: $($disk.MediaType) | Health: $($disk.HealthStatus)""
    try {
        $rel = $disk | Get-StorageReliabilityCounter -ErrorAction Stop
        if ($rel.Temperature) { Write-Host ""  Temperature: $($rel.Temperature) C"" }
        if ($null -ne $rel.Wear) { Write-Host ""  Wear Level: $($rel.Wear)%"" }
        if ($rel.ReadErrorsUncorrected -or $rel.WriteErrorsUncorrected) {
            Write-Host ""  [WARNING] Uncorrected errors - Read: $($rel.ReadErrorsUncorrected), Write: $($rel.WriteErrorsUncorrected)""
        }
    } catch {
        Write-Host '  Detailed reliability counters not available for this disk.'
    }
}
";
    }

    public static string GetScheduleMemoryTestScript()
    {
        return @"
Write-Host 'Scheduling Windows Memory Diagnostic for the next restart...'
mdsched.exe
Write-Host '[OK] Memory test scheduled. Save your work and restart the PC now to run it.'
Write-Host 'Results after reboot appear in Event Viewer under Windows Logs > System, source MemoryDiagnostics-Results.'
";
    }

    public static string GetRecentErrorsScript()
    {
        return @"
function Show-Events($logName, $count) {
    $events = Get-WinEvent -FilterHashtable @{LogName=$logName; Level=1,2} -MaxEvents $count -ErrorAction SilentlyContinue
    foreach ($ev in $events) {
        $msg = ($ev.Message -split ""`n"")[0]
        if ($msg.Length -gt 120) { $msg = $msg.Substring(0, 120) + '...' }
        Write-Host ""[$($ev.TimeCreated)] $($ev.ProviderName): $msg""
    }
}

Write-Host 'Last 10 System log Errors/Critical events:'
Show-Events 'System' 10

Write-Host ''
Write-Host 'Last 5 Application log Errors:'
Show-Events 'Application' 5
";
    }

    public static string GetLargestFilesScript()
    {
        return @"
Write-Host 'Top 20 largest files on C:\ (excluding Windows/Program Files system folders)...'
Get-ChildItem -Path 'C:\' -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '^C:\\(Windows|Program Files|Program Files \(x86\))\\' } |
    Sort-Object Length -Descending |
    Select-Object -First 20 |
    ForEach-Object { Write-Host (""{0,8:N0} MB  {1}"" -f ($_.Length / 1MB), $_.FullName) }
";
    }

    public static string GetStartupItemsScript()
    {
        return @"
Write-Host 'Startup programs (Task Manager > Startup equivalent):'
Get-CimInstance Win32_StartupCommand | ForEach-Object {
    Write-Host ""  $($_.Name) | $($_.Command) | Location: $($_.Location)""
}
";
    }

    public static string GetDisableStartupItemScript(string itemName)
    {
        var escaped = itemName.Replace("'", "''");
        return $@"
$name = '{escaped}'
$found = $false

foreach ($hive in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run')) {{
    if (Test-Path $hive) {{
        $prop = Get-ItemProperty -Path $hive -ErrorAction SilentlyContinue
        if ($prop -and ($prop.PSObject.Properties.Name -contains $name)) {{
            Remove-ItemProperty -Path $hive -Name $name -ErrorAction SilentlyContinue
            Write-Host ""[OK] Removed registry Run entry '$name' from $hive""
            $found = $true
        }}
    }}
}}

foreach ($folder in @([Environment]::GetFolderPath('Startup'), [Environment]::GetFolderPath('CommonStartup'))) {{
    $shortcut = Join-Path $folder ""$name.lnk""
    if (Test-Path $shortcut) {{
        Remove-Item $shortcut -Force -ErrorAction SilentlyContinue
        Write-Host ""[OK] Removed startup shortcut: $shortcut""
        $found = $true
    }}
}}

if (-not $found) {{
    Write-Host ""[WARNING] No startup entry named '$name' was found (check exact name from the list above).""
}}
";
    }

    public static string GetExportDriversScript()
    {
        return @"
$dest = ""C:\DriverBackup\Backup_$(Get-Date -Format 'yyyyMMdd_HHmmss')""
New-Item -ItemType Directory -Path $dest -Force | Out-Null
Write-Host ""Exporting all third-party drivers to $dest ...""
Export-WindowsDriver -Online -Destination $dest | Out-Null
$count = (Get-ChildItem -Path $dest -Directory -ErrorAction SilentlyContinue | Measure-Object).Count
Write-Host ""[OK] Exported $count driver package(s) to: $dest""
Write-Host 'Copy this folder before reinstalling Windows to restore OEM drivers offline.'
";
    }

    public static string GetDuplicateFilesScript()
    {
        return @"
$scanRoots = @(
    [Environment]::GetFolderPath('MyDocuments'),
    [Environment]::GetFolderPath('MyPictures'),
    [Environment]::GetFolderPath('MyVideos'),
    (Join-Path $env:USERPROFILE 'Downloads'),
    (Join-Path $env:USERPROFILE 'Desktop')
) | Where-Object { Test-Path $_ } | Select-Object -Unique

Write-Host 'Scanning for duplicate files in Documents/Pictures/Videos/Downloads/Desktop...'
$files = Get-ChildItem -Path $scanRoots -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Length -gt 1MB } |
    Select-Object -First 2000

$groups = $files | Group-Object Length | Where-Object { $_.Count -gt 1 }
$wastedTotal = 0
$dupGroupsFound = 0

foreach ($g in $groups) {
    $byHash = $g.Group | Group-Object { (Get-FileHash -Path $_.FullName -Algorithm MD5 -ErrorAction SilentlyContinue).Hash }
    foreach ($hashGroup in $byHash) {
        if ($hashGroup.Count -gt 1 -and $hashGroup.Name) {
            $dupGroupsFound++
            $sizeMB = [math]::Round($hashGroup.Group[0].Length / 1MB, 1)
            $wasted = $sizeMB * ($hashGroup.Count - 1)
            $wastedTotal += $wasted
            Write-Host ""Duplicate set ($($hashGroup.Count) copies, $sizeMB MB each):""
            $hashGroup.Group | ForEach-Object { Write-Host ""  $($_.FullName)"" }
        }
    }
}

if ($dupGroupsFound -eq 0) {
    Write-Host 'No duplicate files found in the scanned folders.'
} else {
    Write-Host ""[SUMMARY] $dupGroupsFound duplicate set(s) found - approx $([math]::Round($wastedTotal,1)) MB reclaimable.""
}
";
    }
}
