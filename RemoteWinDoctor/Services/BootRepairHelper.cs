using System.Text.Json;

namespace RemoteWinDoctor.Services;

public class BootScan
{
    /// <summary>UEFI or BIOS.</summary>
    public string Firmware { get; set; } = "";
    public string CurrentWindows { get; set; } = "";
    public bool IsAdmin { get; set; }
    public List<BootDisk> Disks { get; set; } = new();
    public List<string> MenuDevices { get; set; } = new();
}

public class BootDisk
{
    public int Number { get; set; }
    public string Model { get; set; } = "";
    public double SizeGB { get; set; }
    public string PartitionStyle { get; set; } = "";
    public bool IsOffline { get; set; }
    public string OfflineReason { get; set; } = "";
    public bool IsBoot { get; set; }
    public bool HasEsp { get; set; }
    public bool HasActive { get; set; }
    public double FreeMB { get; set; }
    public int UnlabeledBigPartitions { get; set; }
    public List<BootWindows> Windows { get; set; } = new();
}

public class BootWindows
{
    public string Letter { get; set; } = "";
    public int PartitionNumber { get; set; }
    public string Version { get; set; } = "";
    public bool IsCurrent { get; set; }
}

/// <summary>
/// Finds every Windows installation on every disk and repairs what stops one from booting:
/// offline/colliding disks, missing boot files on its own disk, missing boot-menu entry, and
/// MBR disks on UEFI firmware (converted in place with mbr2gpt). Every change first exports
/// the BCD store, never deletes or formats an existing partition, and is re-checked afterwards.
/// Output lines are RWD|status|title|detail (parse with OfficeNetworkWizard.ParseSteps).
/// </summary>
public static class BootRepairHelper
{
    private const string EspGuid = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}";

    private const string Common = @"
$ErrorActionPreference = 'SilentlyContinue'
function Report($status, $title, $detail) { Write-Output ""RWD|$status|$title|$detail"" }
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
function Backup-Bcd {
    $dir = Join-Path $env:ProgramData 'RemoteWinDoctor'
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $file = Join-Path $dir (""BCD-backup-"" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    bcdedit /export $file | Out-Null
    if (Test-Path $file) { Report info 'Boot configuration backed up' ""$file  (restore with: bcdedit /import <file>)"" } else { Report warn 'Could not back up the boot configuration' '' }
}
function Get-FreeLetter { foreach ($l in 'S','T','U','R','Q','P','O','N') { if (-not (Test-Path ""$($l):\"")) { return $l } } }
";

    public static string GetScanScript() => Common + @"
# $env:firmware_type exists in full Windows (PEFirmwareType only inside WinPE)
$fw = if ($env:firmware_type -eq 'UEFI' -or ((bcdedit /enum '{current}') -match 'winload\.efi')) { 'UEFI' } else { 'BIOS' }
$current = $env:SystemDrive
$menu = @((bcdedit /enum osloader) | Select-String '^device\s+partition=(\S+)' | ForEach-Object { $_.Matches[0].Groups[1].Value.ToUpper() })

$disks = foreach ($d in Get-Disk | Sort-Object Number) {
    $parts = @(Get-Partition -DiskNumber $d.Number)
    $wins = foreach ($p in $parts | Where-Object DriveLetter) {
        $l = ""$($p.DriveLetter):""
        if (Test-Path ""$l\Windows\System32\ntoskrnl.exe"") {
            $ver = (Get-Item ""$l\Windows\System32\ntoskrnl.exe"").VersionInfo.ProductVersion
            $build = 0; if ($ver -match '^\d+\.\d+\.(\d+)') { $build = [int]$Matches[1] }
            $name = if ($build -ge 22000) { 'Windows 11' } elseif ($build -ge 10240) { 'Windows 10' } elseif ($build) { 'Windows' } else { 'Windows' }
            [PSCustomObject]@{ Letter = $l; PartitionNumber = $p.PartitionNumber; Version = ""$name (build $build)""; IsCurrent = ($l -eq $current) }
        }
    }
    [PSCustomObject]@{
        Number = $d.Number; Model = ""$($d.FriendlyName)"".Trim(); SizeGB = [math]::Round($d.Size / 1GB, 0)
        PartitionStyle = ""$($d.PartitionStyle)""; IsOffline = [bool]$d.IsOffline; OfflineReason = ""$($d.OfflineReason)""
        IsBoot = [bool]$d.IsBoot
        HasEsp = [bool]($parts | Where-Object { ""$($_.GptType)"" -eq '" + EspGuid + @"' })
        HasActive = [bool]($parts | Where-Object IsActive)
        FreeMB = [math]::Round($d.LargestFreeExtent / 1MB, 0)
        UnlabeledBigPartitions = @($parts | Where-Object { -not $_.DriveLetter -and $_.Type -eq 'Basic' -and $_.Size -gt 20GB }).Count
        Windows = @($wins)
    }
}
[PSCustomObject]@{ Firmware = $fw; CurrentWindows = $current; IsAdmin = $isAdmin; Disks = @($disks); MenuDevices = $menu } | ConvertTo-Json -Depth 5 -Compress
";

    /// <summary>Brings a disk online (new signature first if it collides with a cloned disk) and gives its data partitions letters.</summary>
    public static string GetOnlineScript(int disk) => Common + $@"
if (-not $isAdmin) {{ Report fail 'Administrator rights required' 'Run RemoteWinDoctor as administrator.'; return }}
$d = Get-Disk -Number {disk}
if (""$($d.OfflineReason)"" -match 'Collision|Signature') {{
    # A cloned disk carries the same ID as the original; give it a new one so Windows accepts both
    if (""$($d.PartitionStyle)"" -eq 'MBR') {{ Set-Disk -Number {disk} -Signature (Get-Random -Minimum 1 -Maximum 2147483647) }}
    else {{ Set-Disk -Number {disk} -Guid ([guid]::NewGuid().ToString('B')) }}
    Report info 'Disk ID changed' 'It had the same ID as another disk (a clone). The boot files must be rebuilt afterwards.'
}}
Set-Disk -Number {disk} -IsOffline $false
Set-Disk -Number {disk} -IsReadOnly $false
Start-Sleep -Seconds 2
if ((Get-Disk -Number {disk}).IsOffline) {{ Report fail 'The disk is still offline' ""Reason: $((Get-Disk -Number {disk}).OfflineReason)""; return }}
Report ok 'Disk is online' ''
Get-Partition -DiskNumber {disk} | Where-Object {{ -not $_.DriveLetter -and $_.Type -eq 'Basic' -and $_.Size -gt 1GB }} | ForEach-Object {{
    Add-PartitionAccessPath -DiskNumber {disk} -PartitionNumber $_.PartitionNumber -AssignDriveLetter
}}
$letters = (Get-Partition -DiskNumber {disk} | Where-Object DriveLetter | ForEach-Object {{ ""$($_.DriveLetter):"" }}) -join ', '
if ($letters) {{ Report ok 'Partitions are visible' ""Drive letters: $letters"" }}
";

    /// <summary>
    /// own = put boot files on the Windows partition's own disk, so it boots even if the other disk is removed.
    /// menu = add it to the current boot menu next to the running Windows (the current default stays).
    /// </summary>
    public static string GetBootFilesScript(int disk, string winLetter, bool ownDisk, string firmware) => Common + $@"
if (-not $isAdmin) {{ Report fail 'Administrator rights required' 'Run RemoteWinDoctor as administrator.'; return }}
$w = '{winLetter.TrimEnd('\\')}'
if (-not (Test-Path ""$w\Windows\System32\ntoskrnl.exe"")) {{ Report fail ""No Windows found on $w"" 'Scan again.'; return }}
Backup-Bcd
" + (!ownDisk ? $@"
$out = bcdboot ""$w\Windows"" /d 2>&1 | Out-String
$menu = @((bcdedit /enum osloader) | Select-String '^device\s+partition=(\S+)' | ForEach-Object {{ $_.Matches[0].Groups[1].Value.ToUpper() }})
if ($menu -contains $w.ToUpper()) {{ Report ok ""Windows on $w added to the boot menu"" 'It is listed at startup; the current default Windows is unchanged.' }}
else {{ Report fail 'Could not add it to the boot menu' $out.Trim() }}
" : firmware == "UEFI" ? $@"
$d = Get-Disk -Number {disk}
if (""$($d.PartitionStyle)"" -ne 'GPT') {{ Report fail 'The disk is MBR' 'Convert it to GPT first; UEFI boots only from GPT disks.'; return }}
$esp = Get-Partition -DiskNumber {disk} | Where-Object {{ ""$($_.GptType)"" -eq '{EspGuid}' }} | Select-Object -First 1
if (-not $esp) {{
    if ($d.LargestFreeExtent -lt 300MB) {{ Report fail 'No room for a boot partition' 'The disk has no EFI partition and less than 300 MB unallocated. Shrink a partition by 300 MB in Disk Management, then try again.'; return }}
    $esp = New-Partition -DiskNumber {disk} -Size 300MB
    Format-Volume -Partition $esp -FileSystem FAT32 -NewFileSystemLabel SYSTEM -Confirm:$false | Out-Null
    Set-Partition -DiskNumber {disk} -PartitionNumber $esp.PartitionNumber -GptType '{EspGuid}'
    Report info 'EFI boot partition created' '300 MB, FAT32, from unallocated space.'
}}
$l = Get-FreeLetter
$tmp = Join-Path $env:TEMP 'rwd-diskpart.txt'
""select disk {disk}`nselect partition $($esp.PartitionNumber)`nassign letter=$l"" | Set-Content $tmp -Encoding ascii
diskpart /s $tmp | Out-Null
$out = bcdboot ""$w\Windows"" /s ""$($l):"" /f UEFI 2>&1 | Out-String
$ok = Test-Path ""$($l):\EFI\Microsoft\Boot\bootmgfw.efi""
""select disk {disk}`nselect partition $($esp.PartitionNumber)`nremove letter=$l"" | Set-Content $tmp -Encoding ascii
diskpart /s $tmp | Out-Null
Remove-Item $tmp
if ($ok) {{ Report ok ""Boot files created on disk {disk}"" ""Windows on $w can now start from its own disk. Choose this disk (or 'Windows Boot Manager' on it) in the BIOS boot menu."" }}
else {{ Report fail 'Boot files were not created' $out.Trim() }}
" : $@"
$d = Get-Disk -Number {disk}
if (""$($d.PartitionStyle)"" -ne 'MBR') {{ Report fail 'The disk is GPT' 'Legacy BIOS boots only from MBR disks; switch the BIOS to UEFI mode instead.'; return }}
$out = bcdboot ""$w\Windows"" /s $w /f BIOS 2>&1 | Out-String
$p = Get-Partition -DriveLetter $w.TrimEnd(':')
Set-Partition -DiskNumber {disk} -PartitionNumber $p.PartitionNumber -IsActive $true
if ((Test-Path ""$w\bootmgr"") -and (Get-Partition -DriveLetter $w.TrimEnd(':')).IsActive) {{ Report ok ""Boot files created on disk {disk}"" ""Windows on $w can now start from its own disk. Choose this disk first in the BIOS boot order."" }}
else {{ Report fail 'Boot files were not created' $out.Trim() }}
");

    /// <summary>MBR to GPT in place with Microsoft's mbr2gpt (keeps all data). Validates first.</summary>
    public static string GetConvertScript(int disk) => Common + $@"
if (-not $isAdmin) {{ Report fail 'Administrator rights required' 'Run RemoteWinDoctor as administrator.'; return }}
if (-not (Get-Command mbr2gpt.exe)) {{ Report fail 'mbr2gpt is not available' 'It ships with Windows 10 1703 and later.'; return }}
Backup-Bcd
$v = mbr2gpt /validate /disk:{disk} /allowFullOS 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {{
    $why = ($v -split ""`n"" | Where-Object {{ $_ -match 'fail|cannot|not|error' }} | Select-Object -First 2) -join ' '
    Report fail 'This disk cannot be converted safely' ""mbr2gpt validation failed: $why Common causes: more than 3 primary partitions, or no Windows on the disk. Nothing was changed.""
    return
}}
$c = mbr2gpt /convert /disk:{disk} /allowFullOS 2>&1 | Out-String
if ((Get-Disk -Number {disk}).PartitionStyle -eq 'GPT') {{ Report ok 'Disk converted to GPT' 'All partitions and files were kept. Set the BIOS to UEFI mode (and turn off CSM/Legacy) before starting from this disk.' }}
else {{ Report fail 'Conversion did not complete' (($c -split ""`n"" | Select-Object -Last 3) -join ' ') }}
";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static BootScan? ParseScan(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonSerializer.Deserialize<BootScan>(output[start..(end + 1)], JsonOptions); }
        catch (JsonException) { return null; }
    }
}
