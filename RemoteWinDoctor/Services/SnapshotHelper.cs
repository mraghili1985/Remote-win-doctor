using System.Text.Json;

namespace RemoteWinDoctor.Services;

/// <summary>Point-in-time state of the settings the optimizer changes, measured on the target.</summary>
public class SystemSnapshot
{
    public string Host { get; set; } = "";
    public string TakenAt { get; set; } = "";
    public string Label { get; set; } = "";
    public string SysMainStatus { get; set; } = "";
    public string SysMainStartType { get; set; } = "";
    public string PowerPlanName { get; set; } = "";
    public bool PowerPlanIsHighPerf { get; set; }
    public bool FastStartup { get; set; }
    public double UpdateCacheMB { get; set; }
    public double TempMB { get; set; }
    public double CFreeGB { get; set; }
    public bool PendingReboot { get; set; }
    public int StartupCount { get; set; }
}

public static class SnapshotHelper
{
    public static string GetSnapshotScript()
    {
        return @"
$ErrorActionPreference = 'SilentlyContinue'
function Get-SizeMB($path) {
    $sum = (Get-ChildItem -Path $path -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
    if (-not $sum) { $sum = 0 }
    [math]::Round($sum / 1MB, 1)
}
$sys = Get-Service -Name SysMain
$planLine = ""$(powercfg /getactivescheme)""
$planGuid = if ($planLine -match '([0-9a-fA-F]{8}-[0-9a-fA-F-]{27})') { $Matches[1].ToLower() } else { '' }
$planName = if ($planLine -match '\((.+)\)\s*$') { $Matches[1] } else { $planGuid }
$hb = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' -Name HiberbootEnabled).HiberbootEnabled
$c = Get-Volume -DriveLetter C
$pending = (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') -or
           (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired')
$updateMB = Get-SizeMB 'C:\Windows\SoftwareDistribution\Download'
$tempMB = (Get-SizeMB 'C:\Windows\Temp') + (Get-SizeMB $env:TEMP)

[PSCustomObject]@{
    Host = $env:COMPUTERNAME
    TakenAt = (Get-Date).ToString('yyyy-MM-dd HH:mm')
    SysMainStatus = ""$($sys.Status)""
    SysMainStartType = ""$($sys.StartType)""
    PowerPlanName = $planName
    PowerPlanIsHighPerf = ($planGuid -eq '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c' -or $planGuid -eq 'e9a42b02-d5df-448d-aa00-03f14749eb61')
    FastStartup = ($hb -eq 1)
    UpdateCacheMB = $updateMB
    TempMB = [math]::Round($tempMB, 1)
    CFreeGB = [math]::Round($c.SizeRemaining / 1GB, 1)
    PendingReboot = [bool]$pending
    StartupCount = @(Get-CimInstance Win32_StartupCommand).Count
} | ConvertTo-Json -Compress
";
    }

    public static SystemSnapshot? Parse(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            return JsonSerializer.Deserialize<SystemSnapshot>(output.Substring(start, end - start + 1));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Persists snapshots next to the executable so a "before" survives the target (or this PC)
/// rebooting between optimization and measurement. One file per snapshot:
/// snapshots/HOST_yyyyMMdd-HHmmss_label.json
/// </summary>
public class SnapshotStore
{
    private readonly string _dir;

    public SnapshotStore(string? baseDir = null)
    {
        _dir = Path.Combine(string.IsNullOrWhiteSpace(baseDir) ? AppContext.BaseDirectory : baseDir, "snapshots");
    }

    public void Save(SystemSnapshot snap)
    {
        Directory.CreateDirectory(_dir);
        var file = $"{Sanitize(snap.Host)}_{DateTime.Now:yyyyMMdd-HHmmss}_{snap.Label}.json";
        File.WriteAllText(Path.Combine(_dir, file), JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
    }

    public SystemSnapshot? LatestFor(string host, string label)
    {
        if (!Directory.Exists(_dir)) return null;
        var latest = Directory.GetFiles(_dir, $"{Sanitize(host)}_*_{label}.json")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .FirstOrDefault();
        if (latest == null) return null;
        try
        {
            return JsonSerializer.Deserialize<SystemSnapshot>(File.ReadAllText(latest));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Sanitize(string host) =>
        string.Concat((string.IsNullOrWhiteSpace(host) ? "UNKNOWN" : host).Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == '_' ? '-' : ch));
}
