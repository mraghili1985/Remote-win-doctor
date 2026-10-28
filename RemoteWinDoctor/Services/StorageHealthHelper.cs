using System.Text;
using System.Text.Json;

namespace RemoteWinDoctor.Services;

public class StorageVolume
{
    public string Letter { get; set; } = "";
    public double SizeGB { get; set; }
    public double FreeGB { get; set; }
}

public class StorageDisk
{
    public int Number { get; set; }
    public string Model { get; set; } = "";
    public string Bus { get; set; } = "";
    public string Media { get; set; } = "";
    public double SizeGB { get; set; }
    public bool IsSystem { get; set; }
    public string WindowsHealth { get; set; } = "";
    public List<StorageVolume> Volumes { get; set; } = new();
    /// <summary>NVMe, ATA, Windows (reliability counters only) or None.</summary>
    public string Source { get; set; } = "None";
    /// <summary>Good, Caution, Bad or Unknown.</summary>
    public string Status { get; set; } = "Unknown";
    /// <summary>Remaining life in percent; only for SSDs that report it.</summary>
    public int? HealthPercent { get; set; }
    public long? PowerOnHours { get; set; }
    public long? PowerCycles { get; set; }
    public int? TemperatureC { get; set; }
    public double? WrittenTB { get; set; }
    public long? UnsafeShutdowns { get; set; }
    public long? MediaErrors { get; set; }
    public long? Reallocated { get; set; }
    public long? Pending { get; set; }
    public long? Uncorrectable { get; set; }
    public int? AvailableSpare { get; set; }
    public List<string> Notes { get; set; } = new();

    public string Kind => Media == "HDD" ? "HDD" : Bus == "NVMe" || Source == "NVMe" ? "NVMe SSD" : Media == "SSD" ? "SATA SSD" : Media.Length > 0 && Media != "Unspecified" ? Media : Bus;
}

/// <summary>
/// Per-disk health like CrystalDiskInfo: reads the NVMe SMART/health log page through
/// IOCTL_STORAGE_QUERY_PROPERTY, and the ATA SMART attribute table through root\wmi. Windows'
/// own Get-StorageReliabilityCounter is only a fallback because it is often empty or wrong
/// (e.g. temperature 1 and wear 0 behind Intel RST). Both raw reads need administrator rights.
/// </summary>
public static class StorageHealthHelper
{
    public static string GetStorageScript() => @"
$ErrorActionPreference = 'SilentlyContinue'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($isAdmin -and -not ('RwdNvme' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class RwdNvme {
    [DllImport(""kernel32.dll"", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport(""kernel32.dll"", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);
    static void Put(byte[] b, int o, int v) { BitConverter.GetBytes(v).CopyTo(b, o); }

    // NVMe SMART / Health Information log (log page 0x02), 512 bytes, or null
    public static byte[] ReadHealthLog(int disk) {
        string path = ""\\\\.\\PhysicalDrive"" + disk;
        SafeFileHandle h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        using (h) {
            // StorageDeviceProtocolSpecificProperty (50), then StorageAdapterProtocolSpecificProperty (49)
            foreach (int prop in new int[] { 50, 49 }) {
                byte[] buf = new byte[8 + 40 + 512];
                Put(buf, 0, prop);          // PropertyId; QueryType 0 = standard
                Put(buf, 8, 3);             // ProtocolType = NVMe
                Put(buf, 12, 2);            // DataType = log page
                Put(buf, 16, 2);            // log page 0x02
                Put(buf, 24, 40);           // data offset (after STORAGE_PROTOCOL_SPECIFIC_DATA)
                Put(buf, 28, 512);          // data length
                int ret;
                if (!DeviceIoControl(h, 0x2D1400, buf, buf.Length, buf, buf.Length, out ret, IntPtr.Zero)) continue;
                int off = BitConverter.ToInt32(buf, 8 + 16);
                if (off <= 0 || 8 + off + 512 > buf.Length) continue;
                byte[] log = new byte[512];
                Array.Copy(buf, 8 + off, log, 0, 512);
                foreach (byte x in log) { if (x != 0) return log; }
            }
        }
        return null;
    }
}
'@
}

$diskDrives = @(Get-CimInstance Win32_DiskDrive)
$ataData = @(Get-CimInstance -Namespace root\wmi -ClassName MSStorageDriver_FailurePredictData)
$ataThr = @(Get-CimInstance -Namespace root\wmi -ClassName MSStorageDriver_FailurePredictThresholds)
$ataStatus = @(Get-CimInstance -Namespace root\wmi -ClassName MSStorageDriver_FailurePredictStatus)
$systemDisk = (Get-Partition -DriveLetter C).DiskNumber

$result = foreach ($pd in Get-PhysicalDisk | Sort-Object { [int]$_.DeviceId }) {
    $n = [int]$pd.DeviceId
    $d = [ordered]@{
        Number = $n; Model = ""$($pd.FriendlyName)"".Trim(); Bus = ""$($pd.BusType)""; Media = ""$($pd.MediaType)""
        SizeGB = [math]::Round($pd.Size / 1GB, 0); IsSystem = ($n -eq $systemDisk); WindowsHealth = ""$($pd.HealthStatus)""
        Volumes = @(); Source = 'None'; Status = 'Unknown'; HealthPercent = $null; PowerOnHours = $null; PowerCycles = $null
        TemperatureC = $null; WrittenTB = $null; UnsafeShutdowns = $null; MediaErrors = $null; Reallocated = $null
        Pending = $null; Uncorrectable = $null; AvailableSpare = $null; Notes = @()
    }
    $d.Volumes = @(Get-Partition -DiskNumber $n | Where-Object DriveLetter | ForEach-Object {
        $v = Get-Volume -DriveLetter $_.DriveLetter
        [PSCustomObject]@{ Letter = ""$($_.DriveLetter):""; SizeGB = [math]::Round($v.Size / 1GB, 1); FreeGB = [math]::Round($v.SizeRemaining / 1GB, 1) }
    })

    if (-not $isAdmin) { $d.Notes += 'SMART details need administrator rights. Run RemoteWinDoctor as administrator.' }

    # NVMe health log
    $log = $null
    if ($isAdmin -and ('RwdNvme' -as [type])) { $log = [RwdNvme]::ReadHealthLog($n) }
    if ($log) {
        $d.Source = 'NVMe'
        $crit = $log[0]
        $d.TemperatureC = [BitConverter]::ToUInt16($log, 1) - 273
        $d.AvailableSpare = $log[3]; $spareThr = $log[4]
        $used = $log[5]
        $d.HealthPercent = [math]::Max(0, 100 - $used)
        $d.WrittenTB = [math]::Round([BitConverter]::ToUInt64($log, 48) * 512000 / 1e12, 1)
        $d.PowerCycles = [BitConverter]::ToUInt64($log, 112)
        $d.PowerOnHours = [BitConverter]::ToUInt64($log, 128)
        $d.UnsafeShutdowns = [BitConverter]::ToUInt64($log, 144)
        $d.MediaErrors = [BitConverter]::ToUInt64($log, 160)
        if ($crit -ne 0 -or $d.MediaErrors -gt 0 -or ($spareThr -gt 0 -and $d.AvailableSpare -lt $spareThr)) { $d.Status = 'Bad' }
        elseif ($used -ge 90 -or $d.TemperatureC -ge 75) { $d.Status = 'Caution' }
        else { $d.Status = 'Good' }
        if ($crit -ne 0) { $d.Notes += ""The drive reports a critical warning (code $crit)."" }
        if ($d.MediaErrors -gt 0) { $d.Notes += ""$($d.MediaErrors) media errors: data on this drive is at risk; back it up."" }
        if ($used -ge 90) { $d.Notes += 'Rated write life is almost used up.' }
    }

    # ATA SMART table (SATA HDD/SSD)
    if ($d.Source -eq 'None' -and $isAdmin) {
        $pnp = ($diskDrives | Where-Object Index -eq $n).PNPDeviceID
        $match = { param($inst) ($inst -replace '_\d+$', '') -eq $pnp }
        $data = $ataData | Where-Object { & $match $_.InstanceName } | Select-Object -First 1
        if ($pnp -and $data) {
            $d.Source = 'ATA'
            $thr = $ataThr | Where-Object { & $match $_.InstanceName } | Select-Object -First 1
            $predict = ($ataStatus | Where-Object { & $match $_.InstanceName } | Select-Object -First 1).PredictFailure
            $vs = [byte[]]$data.VendorSpecific
            $tv = if ($thr) { [byte[]]$thr.VendorSpecific } else { $null }
            $attrs = @{}; $belowThreshold = @()
            for ($i = 2; $i + 12 -le $vs.Length; $i += 12) {
                $id = $vs[$i]; if ($id -eq 0) { continue }
                $raw = [uint64]0; for ($k = 5; $k -ge 0; $k--) { $raw = $raw * 256 + $vs[$i + 5 + $k] }
                $attrs[[int]$id] = @{ Current = $vs[$i + 3]; Raw = $raw; Raw0 = $vs[$i + 5] }
                if ($tv) {
                    for ($j = 2; $j + 12 -le $tv.Length; $j += 12) {
                        if ($tv[$j] -eq $id -and $tv[$j + 1] -gt 0 -and $vs[$i + 3] -gt 0 -and $vs[$i + 3] -le $tv[$j + 1]) { $belowThreshold += $id }
                    }
                }
            }
            if ($attrs[9]) { $d.PowerOnHours = [int64]($attrs[9].Raw -band 0xFFFFFFFF) }
            if ($attrs[12]) { $d.PowerCycles = [int64]$attrs[12].Raw }
            foreach ($t in 194, 190) { if ($attrs[$t] -and $attrs[$t].Raw0 -gt 0 -and $attrs[$t].Raw0 -lt 100) { $d.TemperatureC = [int]$attrs[$t].Raw0; break } }
            if ($attrs[5]) { $d.Reallocated = [int64]($attrs[5].Raw -band 0xFFFFFFFF) }
            if ($attrs[197]) { $d.Pending = [int64]($attrs[197].Raw -band 0xFFFFFFFF) }
            if ($attrs[198]) { $d.Uncorrectable = [int64]($attrs[198].Raw -band 0xFFFFFFFF) }
            if ($attrs[192]) { $d.UnsafeShutdowns = [int64]($attrs[192].Raw -band 0xFFFFFFFF) }
            if ($d.Media -ne 'HDD') {
                foreach ($l in 231, 233, 177, 202, 169) { if ($attrs[$l] -and $attrs[$l].Current -le 100) { $d.HealthPercent = [int]$attrs[$l].Current; break } }
                if ($attrs[241]) { $d.WrittenTB = [math]::Round($attrs[241].Raw * 512 / 1e12, 1) }
            }
            if ($predict -or $belowThreshold.Count -gt 0) { $d.Status = 'Bad' }
            elseif ($d.Reallocated -gt 0 -or $d.Pending -gt 0 -or $d.Uncorrectable -gt 0) { $d.Status = 'Caution' }
            else { $d.Status = 'Good' }
            if ($predict) { $d.Notes += 'The drive predicts its own failure. Back up the data and replace it.' }
            if ($belowThreshold.Count) { $d.Notes += ""SMART attribute(s) $($belowThreshold -join ', ') are below the failure threshold."" }
            if ($d.Pending -gt 0) { $d.Notes += ""$($d.Pending) sectors are waiting to be remapped (unstable sectors)."" }
            if ($d.Reallocated -gt 0) { $d.Notes += ""$($d.Reallocated) bad sectors have already been replaced."" }
            if ($d.Uncorrectable -gt 0) { $d.Notes += ""$($d.Uncorrectable) sectors could not be read at all."" }
        }
    }

    # Fallback: Windows reliability counters, only values that look real
    if ($d.Source -eq 'None') {
        $c = $pd | Get-StorageReliabilityCounter
        if ($c) {
            if ($c.PowerOnHours -gt 0) { $d.PowerOnHours = [int64]$c.PowerOnHours; $d.Source = 'Windows' }
            if ($c.Temperature -ge 10 -and $c.Temperature -lt 100) { $d.TemperatureC = [int]$c.Temperature; $d.Source = 'Windows' }
            if ($c.StartStopCycleCount -gt 0) { $d.PowerCycles = [int64]$c.StartStopCycleCount }
        }
        if ($isAdmin) {
            if ($d.Bus -eq 'RAID') { $d.Notes += 'This drive sits behind a RAID/Intel RST controller, which hides SMART data from Windows.' }
            elseif ($d.Bus -eq 'USB') { $d.Notes += 'USB enclosures usually do not pass SMART data through.' }
            else { $d.Notes += 'The drive did not return SMART data.' }
        }
        if ($d.WindowsHealth -eq 'Unhealthy') { $d.Status = 'Bad' } elseif ($d.WindowsHealth -eq 'Warning') { $d.Status = 'Caution' }
    }
    [PSCustomObject]$d
}

ConvertTo-Json -InputObject @($result) -Depth 4 -Compress
";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static List<StorageDisk>? Parse(string output)
    {
        var start = output.IndexOf('[');
        var end = output.LastIndexOf(']');
        if (start < 0 || end <= start) return null;
        try { return JsonSerializer.Deserialize<List<StorageDisk>>(output[start..(end + 1)], JsonOptions); }
        catch (JsonException) { return null; }
    }

    public static string FormatHours(long hours)
    {
        var days = hours / 24.0;
        return days >= 365 ? $"{hours:N0} h ({days / 365:0.0} years)" : $"{hours:N0} h ({days:0} days)";
    }

    /// <summary>Plain-text report for the toolbox output panel.</summary>
    public static string ToText(List<StorageDisk> disks)
    {
        var sb = new StringBuilder();
        foreach (var d in disks)
        {
            sb.AppendLine($"Disk {d.Number}: {d.Model} | {d.Kind} | {d.SizeGB:0} GB{(d.IsSystem ? " | Windows" : "")}");
            sb.AppendLine($"  Status: {d.Status}{(d.HealthPercent is int h ? $" | Health {h}%" : "")} (source: {d.Source})");
            if (d.PowerOnHours is long poh) sb.AppendLine($"  Power-on time: {FormatHours(poh)}");
            if (d.TemperatureC is int t) sb.AppendLine($"  Temperature: {t} °C");
            if (d.PowerCycles is long pc) sb.AppendLine($"  Power cycles: {pc:N0}");
            if (d.WrittenTB is double tb) sb.AppendLine($"  Total written: {tb:0.0} TB");
            if (d.Reallocated is long r) sb.AppendLine($"  Reallocated sectors: {r}");
            if (d.Pending is long p) sb.AppendLine($"  Pending sectors: {p}");
            if (d.Uncorrectable is long u) sb.AppendLine($"  Uncorrectable sectors: {u}");
            if (d.MediaErrors is long me) sb.AppendLine($"  Media errors: {me}");
            if (d.UnsafeShutdowns is long us) sb.AppendLine($"  {(d.Source == "ATA" ? "Emergency head retracts" : "Unsafe shutdowns")}: {us:N0}");
            foreach (var v in d.Volumes) sb.AppendLine($"  {v.Letter} {v.FreeGB:0.#} GB free of {v.SizeGB:0.#} GB");
            foreach (var n in d.Notes) sb.AppendLine($"  Note: {n}");
        }
        return sb.ToString().TrimEnd();
    }
}
