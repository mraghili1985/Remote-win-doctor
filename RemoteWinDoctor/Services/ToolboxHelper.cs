namespace RemoteWinDoctor.Services;

/// <summary>One row of <see cref="ToolboxHelper.GetStartupEntriesScript"/> output.</summary>
public class StartupEntry
{
    public string Name { get; set; } = "";
    /// <summary>Value name under the StartupApproved key (the Run value name, or the file name for Startup-folder items).</summary>
    public string ValueName { get; set; } = "";
    public string Command { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>"All users" or the account name the entry belongs to.</summary>
    public string Scope { get; set; } = "";
    public string ApprovedKey { get; set; } = "";
    public bool Enabled { get; set; }
}

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

    /// <summary>
    /// Lists startup programs the way Task Manager's Startup tab does, as a JSON array of
    /// <see cref="StartupEntry"/>. On/off state comes from the Explorer\StartupApproved keys
    /// (first byte even = enabled, odd = disabled). Every loaded user hive is read, not HKCU,
    /// because over WinRM HKCU is the remote login account rather than the person at the PC.
    /// </summary>
    public static string GetStartupEntriesScript()
    {
        return @"
$ErrorActionPreference = 'SilentlyContinue'
$approvedRel = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved'
$items = New-Object System.Collections.ArrayList
$shell = New-Object -ComObject WScript.Shell

function Get-Target([string]$cmd) {
    if ($cmd -match '^\s*""([^""]+)""') { return $Matches[1] }
    if ($cmd -match '^\s*(.+?\.exe)') { return $Matches[1] }
    return $cmd.Trim()
}
function Test-Enabled([string]$key, [string]$valueName) {
    $v = (Get-ItemProperty -LiteralPath $key -Name $valueName).$valueName
    if ($v -is [byte[]] -and $v.Length -gt 0) { return ($v[0] % 2) -eq 0 }
    return $true
}
function Add-Entry($name, $valueName, $cmd, $target, $scope, $approvedKey) {
    $path = [Environment]::ExpandEnvironmentVariables($target)
    $vi = $null
    if ($path -and (Test-Path -LiteralPath $path -PathType Leaf)) { $vi = (Get-Item -LiteralPath $path).VersionInfo }
    [void]$items.Add([PSCustomObject]@{
        Name = $name; ValueName = $valueName; Command = ""$cmd""
        Publisher = ""$($vi.CompanyName)"".Trim(); Description = ""$($vi.FileDescription)"".Trim()
        Scope = $scope; ApprovedKey = $approvedKey; Enabled = (Test-Enabled $approvedKey $valueName)
    })
}
function Read-RunKey($runKey, $approvedKey, $scope) {
    $k = Get-Item -LiteralPath $runKey
    if (-not $k) { return }
    foreach ($n in $k.GetValueNames()) {
        if (-not $n) { continue }
        $cmd = ""$($k.GetValue($n))""
        Add-Entry $n $n $cmd (Get-Target $cmd) $scope $approvedKey
    }
}
function Read-Folder($folder, $approvedKey, $scope) {
    if (-not $folder -or -not (Test-Path -LiteralPath $folder)) { return }
    foreach ($f in Get-ChildItem -LiteralPath $folder -File | Where-Object { $_.Name -ne 'desktop.ini' }) {
        $target = $f.FullName
        if ($f.Extension -eq '.lnk') { $t = $shell.CreateShortcut($f.FullName).TargetPath; if ($t) { $target = $t } }
        Add-Entry $f.BaseName $f.Name $target $target $scope $approvedKey
    }
}

$hklm = 'Registry::HKEY_LOCAL_MACHINE'
Read-RunKey ""$hklm\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"" ""$hklm\$approvedRel\Run"" 'All users'
Read-RunKey ""$hklm\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"" ""$hklm\$approvedRel\Run32"" 'All users'
Read-Folder ([Environment]::GetFolderPath('CommonStartup')) ""$hklm\$approvedRel\StartupFolder"" 'All users'

foreach ($u in Get-ChildItem 'Registry::HKEY_USERS' | Where-Object { $_.PSChildName -match '^S-1-5-21-[\d-]+$' }) {
    $sid = $u.PSChildName
    $hku = ""Registry::HKEY_USERS\$sid""
    $user = try { ((New-Object System.Security.Principal.SecurityIdentifier($sid)).Translate([System.Security.Principal.NTAccount]).Value -split '\\')[-1] } catch { $sid }
    Read-RunKey ""$hku\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"" ""$hku\$approvedRel\Run"" $user
    $profileDir = (Get-ItemProperty -LiteralPath ""$hklm\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$sid"").ProfileImagePath
    if ($profileDir) { Read-Folder ""$profileDir\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup"" ""$hku\$approvedRel\StartupFolder"" $user }
}

ConvertTo-Json -InputObject @($items | Sort-Object Name) -Depth 3 -Compress
";
    }

    /// <summary>
    /// Turns a startup entry on or off exactly like Task Manager: writes the StartupApproved flag
    /// and leaves the program's own Run value or shortcut untouched, so it is fully reversible.
    /// </summary>
    public static string GetSetStartupEntryScript(string approvedKey, string valueName, bool enable)
    {
        static string Q(string s) => s.Replace("'", "''");
        return $@"
$key = '{Q(approvedKey)}'
$name = '{Q(valueName)}'
if (-not (Test-Path -LiteralPath $key)) {{ New-Item -Path $key -Force | Out-Null }}
$bytes = {(enable ? "[byte[]](2,0,0,0,0,0,0,0,0,0,0,0)" : "[byte[]](3,0,0,0) + [BitConverter]::GetBytes([DateTime]::Now.ToFileTime())")}
New-ItemProperty -LiteralPath $key -Name $name -PropertyType Binary -Value $bytes -Force | Out-Null
Write-Output 'OK'
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
