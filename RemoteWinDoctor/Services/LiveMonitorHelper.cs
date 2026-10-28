using System.Text.Json;

namespace RemoteWinDoctor.Services;

public class LiveProcess
{
    public string N { get; set; } = "";
    public double C { get; set; }
}

public class LiveSample
{
    public double Cpu { get; set; }
    public double Ram { get; set; }
    public double Disk { get; set; }
    public List<LiveProcess> Top { get; set; } = new();
}

public static class LiveMonitorHelper
{
    /// <summary>
    /// Emits one compact JSON line per interval until the process is killed.
    /// Uses Win32_PerfFormattedData_* CIM classes rather than Get-Counter, whose counter
    /// paths are translated on non-English Windows.
    /// </summary>
    // The CIM queries themselves take ~1s, so a 1s sleep yields roughly one sample every 2s.
    public static string GetSamplingScript(int intervalSeconds = 1)
    {
        return @"
$ErrorActionPreference = 'SilentlyContinue'
$lp = (Get-CimInstance Win32_ComputerSystem).NumberOfLogicalProcessors
if (-not $lp) { $lp = 1 }
while ($true) {
    $cpu = (Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter ""Name='_Total'"").PercentProcessorTime
    $os = Get-CimInstance Win32_OperatingSystem -Property FreePhysicalMemory, TotalVisibleMemorySize
    $ram = [math]::Round((1 - $os.FreePhysicalMemory / $os.TotalVisibleMemorySize) * 100, 1)
    $d = Get-CimInstance Win32_PerfFormattedData_PerfDisk_PhysicalDisk -Filter ""Name='_Total'""
    $disk = [math]::Max(0, [math]::Min(100, 100 - $d.PercentIdleTime))
    # Instances of one app appear as name, name#1, name#2...; sum them per app
    $top = @(Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter 'PercentProcessorTime > 0' -Property Name, PercentProcessorTime |
        Where-Object { $_.Name -ne '_Total' -and $_.Name -ne 'Idle' } |
        Group-Object { $_.Name -replace '#\d+$', '' } |
        ForEach-Object { @{ n = $_.Name; c = [math]::Round(($_.Group | Measure-Object PercentProcessorTime -Sum).Sum / $lp, 0) } } |
        Sort-Object { $_.c } -Descending | Select-Object -First 3)
    [PSCustomObject]@{ cpu = $cpu; ram = $ram; disk = $disk; top = $top } | ConvertTo-Json -Compress -Depth 3
    Start-Sleep -Seconds " + intervalSeconds + @"
}
";
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static LiveSample? Parse(string line)
    {
        var start = line.IndexOf('{');
        if (start < 0) return null;
        try
        {
            return JsonSerializer.Deserialize<LiveSample>(line[start..], Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
