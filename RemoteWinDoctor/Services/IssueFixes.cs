namespace RemoteWinDoctor.Services;

/// <summary>A diagnostics finding; Id matches the ones emitted by DiagnosticEngine's Add-Issue.</summary>
public record HealthIssue(string Id, string Text, string Detail);

/// <summary>
/// One-click fixes and plain advice for diagnostics findings. Fix scripts only touch Windows
/// settings and system caches (never personal files or the Recycle Bin), re-check the result
/// and report RWD|status|title|detail lines (parse with OfficeNetworkWizard.ParseSteps).
/// </summary>
public static class IssueFixes
{
    public static string? FixLabel(string id) => id switch
    {
        "fast-startup" => "Turn off",
        "c-space" => "Free up space",
        "devices" => "Rescan devices",
        _ => null
    };

    public static string? ConfirmText(string id) => id switch
    {
        "fast-startup" => "Turn off Fast Startup? Shutdowns become full shutdowns; startup may take a few seconds longer.",
        "c-space" => "Clean system caches on C:? This empties the Windows and user Temp folders, the Windows Update download cache and the Delivery Optimization cache. Personal files and the Recycle Bin are not touched.",
        "devices" => "Ask Windows to rescan all hardware? Devices may briefly disconnect and reconnect.",
        _ => null
    };

    /// <summary>What to tell the user when software can't fix it (or as a hint next to the fix).</summary>
    public static string? Advice(string id) => id switch
    {
        "ram-single" => "Install a second RAM module of the same size and speed in the empty slot of the other channel to double memory bandwidth.",
        "ram-low" => "Add RAM: 8 GB is the practical minimum for Windows 11, 16 GB for office work with many browser tabs.",
        "disk-unhealthy" => "Back up important data now and plan to replace the disk. See the Storage section for SMART details.",
        "disk-smart" => "Back up important data now. Replace the disk if bad or pending sectors keep increasing.",
        "sys-hdd" => "Moving Windows to an SSD is the single biggest speed upgrade for this PC.",
        "devices" => "If a device still shows \"no driver installed\" after rescanning, install its driver in the Driver Installer tab.",
        "events" => "Use \"Show all errors\" in the Health Toolbox for the full list; repeated errors from the same source point to the faulty component.",
        _ => null
    };

    public static string? GetFixScript(string id) => id switch
    {
        "fast-startup" => @"
$p = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power'
Set-ItemProperty -Path $p -Name HiberbootEnabled -Value 0 -Type DWord -ErrorAction SilentlyContinue
if ((Get-ItemProperty $p).HiberbootEnabled -eq 0) { Write-Output 'RWD|ok|Fast Startup turned off|Takes effect from the next shutdown.' }
else { Write-Output 'RWD|fail|Could not turn off Fast Startup|The setting did not change; administrator rights are required.' }
",
        "c-space" => @"
$ErrorActionPreference = 'SilentlyContinue'
$before = (Get-Volume -DriveLetter C).SizeRemaining
Remove-Item 'C:\Windows\Temp\*' -Recurse -Force
Remove-Item ""$env:TEMP\*"" -Recurse -Force
Stop-Service wuauserv, bits -Force
Remove-Item 'C:\Windows\SoftwareDistribution\Download\*' -Recurse -Force
Start-Service bits, wuauserv
Delete-DeliveryOptimizationCache -Force
$after = (Get-Volume -DriveLetter C).SizeRemaining
$freed = [math]::Round(($after - $before) / 1GB, 2)
$free = [math]::Round($after / 1GB, 1)
if ($freed -gt 0) { Write-Output ""RWD|ok|Freed $freed GB on C:|$free GB is free now. Files in use were skipped."" }
else { Write-Output ""RWD|warn|No significant space freed|$free GB free. The space is used by your own files or programs; check Largest files in the Health Toolbox."" }
",
        "devices" => @"
$ErrorActionPreference = 'SilentlyContinue'
pnputil /scan-devices | Out-Null
Start-Sleep -Seconds 5
$left = @(Get-CimInstance Win32_PnPEntity | Where-Object { $_.ConfigManagerErrorCode -notin 0, 22, 45 })
if ($left.Count -eq 0) { Write-Output 'RWD|ok|All devices are working now|Windows found drivers for every device after the rescan.' }
else { Write-Output ""RWD|warn|$($left.Count) device(s) still have problems|$(($left | Select-Object -First 5 | ForEach-Object { $_.Name }) -join '; ')"" }
",
        _ => null
    };
}
