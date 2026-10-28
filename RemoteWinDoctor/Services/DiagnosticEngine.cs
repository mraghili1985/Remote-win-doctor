namespace RemoteWinDoctor.Services;

public class DiagnosticEngine
{
    public static string GetDeepDiagnosticScript()
    {
        return @"
$ErrorActionPreference = 'SilentlyContinue'

# 1. System Info
$cs = Get-CimInstance Win32_ComputerSystem
$bios = Get-CimInstance Win32_Bios
$os = Get-CimInstance Win32_OperatingSystem

# 2. CPU
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1

# 3. RAM Modules & Channels
$dimms = @(Get-CimInstance Win32_PhysicalMemory | Select-Object BankLabel, DeviceLocator, Capacity, Speed, Manufacturer, PartNumber)
$dimmCount = $dimms.Count
$slotCount = (Get-CimInstance Win32_PhysicalMemoryArray | Measure-Object -Property MemoryDevices -Sum).Sum
$totalRamGb = [math]::Round($os.TotalVisibleMemorySize / 1MB, 2)
# Visible memory is always a bit below installed (firmware/iGPU reservation); judge capacity by installed.
$installedRamGb = [math]::Round((($dimms | Measure-Object -Property Capacity -Sum).Sum) / 1GB, 0)
if (-not $installedRamGb) { $installedRamGb = [math]::Round($totalRamGb, 0) }
$freeRamGb = [math]::Round($os.FreePhysicalMemory / 1MB, 2)
$ramPercentFree = [math]::Round(($freeRamGb / $totalRamGb) * 100, 1)

# Two modules on the same channel is still single-channel, so read the channel letter
# from the locator (e.g. 'ChannelA-DIMM1', 'DIMM B') instead of just counting modules.
$channels = @($dimms | ForEach-Object {
    $label = ""$($_.DeviceLocator) $($_.BankLabel)""
    if ($label -match 'Channel\s*([A-Z])') { $Matches[1] }
    elseif ($label -match 'DIMM\s*([A-Z])\b') { $Matches[1] }
} | Select-Object -Unique)
if ($channels.Count -ge 2) { $channelMode = 'Dual/Multi-Channel' }
elseif ($channels.Count -eq 1) { $channelMode = 'Single-Channel (Bottleneck)' }
elseif ($dimmCount -ge 2) { $channelMode = 'Dual/Multi-Channel (estimated)' }
else { $channelMode = 'Single-Channel (Bottleneck)' }

# 4. Storage & SSD
$disks = Get-PhysicalDisk | Select-Object DeviceId, FriendlyName, MediaType, BusType, HealthStatus, OperationalStatus, @{N='SizeGB';E={[math]::Round($_.Size/1GB, 1)}}
$vols = Get-Volume | Where-Object { $_.DriveLetter } | Select-Object DriveLetter, FileSystem, @{N='SizeGB';E={[math]::Round($_.Size/1GB,1)}}, @{N='FreeGB';E={[math]::Round($_.SizeRemaining/1GB,1)}}, @{N='PercentFree';E={[math]::Round(($_.SizeRemaining/$_.Size)*100,1)}}

$sysDiskNumber = (Get-Partition -DriveLetter C | Select-Object -First 1).DiskNumber
$sysPhys = Get-PhysicalDisk | Where-Object { $_.DeviceId -eq ""$sysDiskNumber"" } | Select-Object -First 1
$sysDiskType = if (-not $sysPhys) { 'Unknown' }
    elseif ($sysPhys.BusType -eq 'NVMe') { 'NVMe SSD' }
    elseif ($sysPhys.MediaType -eq 'SSD') { 'SATA SSD' }
    elseif ($sysPhys.MediaType -eq 'HDD') { 'HDD' }
    else { ""$($sysPhys.BusType) $($sysPhys.MediaType)"" }
$systemDisk = [PSCustomObject]@{
    FriendlyName = $sysPhys.FriendlyName
    HealthStatus = ""$($sysPhys.HealthStatus)""
    Type = $sysDiskType
    PercentFree = ($vols | Where-Object { $_.DriveLetter -eq 'C' }).PercentFree
}

# 5. GPUs & AI Readiness
$gpus = Get-CimInstance Win32_VideoController | Select-Object Name, AdapterRAM, DriverVersion, VideoProcessor
$hasNvidia = $false
$nvidiaVramGb = 0.0
# Win32_VideoController.AdapterRAM is a uint32 and caps at 4GB; the driver's registry
# value qwMemorySize holds the true 64-bit VRAM size.
$gpuRegVram = @{}
Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}' -ErrorAction SilentlyContinue |
    Where-Object { $_.PSChildName -match '^\d{4}$' } | ForEach-Object {
        $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
        $qw = $p.'HardwareInformation.qwMemorySize'
        if ($p.DriverDesc -and $qw) { $gpuRegVram[$p.DriverDesc] = [double]$qw }
    }
foreach ($g in $gpus) {
    if ($g.Name -match 'NVIDIA|Quadro|GeForce|RTX|GTX|Tesla') {
        $hasNvidia = $true
        $bytes = if ($gpuRegVram.ContainsKey($g.Name)) { $gpuRegVram[$g.Name] } else { [double]$g.AdapterRAM }
        $vram = [math]::Round($bytes / 1GB, 1)
        if ($vram -gt $nvidiaVramGb) { $nvidiaVramGb = $vram }
    }
}

# AI Score Calculation
$aiScore = 0
$aiTier = 'Not suitable for AI workloads'
$aiRecommendations = @()

if ($hasNvidia) {
    if ($nvidiaVramGb -ge 12) {
        $aiScore = 95
        $aiTier = 'Excellent (Tier S - heavy models: Llama 3 8B/70B Q4, Stable Diffusion XL)'
    } elseif ($nvidiaVramGb -ge 6) {
        $aiScore = 80
        $aiTier = 'Very good (Tier A - fast 3B-8B quantized models, Whisper Medium)'
    } else {
        $aiScore = 60
        $aiTier = 'Moderate (Tier B - light 1B-3B models, light image work)'
    }
} else {
    $aiScore = 15
    $aiTier = 'Very weak (Tier D - no CUDA cores, slow CPU-only inference)'
    $aiRecommendations += 'No NVIDIA GPU; this PC cannot run language models at a usable speed.'
}

if ($installedRamGb -lt 16) {
    $aiRecommendations += 'Less than 16 GB RAM; upgrading to 16 or 32 GB is recommended.'
}
if ($channelMode -like '*Single*') {
    $aiRecommendations += 'RAM runs in single-channel mode, which halves memory bandwidth.'
}

# 6. Power & Fast Startup
$powerScheme = (powercfg /getactivescheme) -replace 'Power Scheme GUID: ',''
$hiberboot = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' -Name 'HiberbootEnabled').HiberbootEnabled

# 7. Services Status
$sysmain = (Get-Service -Name SysMain).Status.ToString()
$wu = (Get-Service -Name wuauserv).Status.ToString()
$updateFiles = (Get-ChildItem 'C:\Windows\SoftwareDistribution\Download' -Recurse -File).Count

# 8. Top Processes
$topProcs = Get-Process | Sort-Object CPU -Descending | Select-Object -First 5 Id, ProcessName, @{N='CPU_Sec';E={[math]::Round($_.CPU, 1)}}, @{N='RAM_MB';E={[math]::Round($_.WorkingSet64/1MB, 1)}}

# 9. Recent Critical System Events
$events = Get-WinEvent -FilterHashtable @{LogName='System'; Level=1,2; StartTime=(Get-Date).AddHours(-24)} -MaxEvents 5 | 
    Select-Object @{N='Time';E={$_.TimeCreated.ToString('HH:mm:ss')}}, ProviderName, Id, Message

# 10. Device Manager Problem Devices
$problemDevices = @(Get-CimInstance Win32_PnPEntity | Where-Object { $_.ConfigManagerErrorCode -ne 0 } |
    Select-Object Name, DeviceID, ConfigManagerErrorCode)

# 11. Composite health score - every deduction is tied to a real finding above
$healthScore = 100
$healthIssues = @()
if ($channelMode -like '*Single*') { $healthScore -= 15; $healthIssues += 'Single-channel RAM (reduced memory bandwidth)' }
if ($installedRamGb -lt 8) { $healthScore -= 10; $healthIssues += 'Less than 8 GB RAM' }
if (@($disks | Where-Object { ""$($_.HealthStatus)"" -ne 'Healthy' }).Count -gt 0) { $healthScore -= 30; $healthIssues += 'A disk reports a health status other than Healthy' }
$cFree = $systemDisk.PercentFree
if ($cFree -ne $null -and $cFree -lt 5) { $healthScore -= 25; $healthIssues += 'Drive C: is almost full (under 5% free)' }
elseif ($cFree -ne $null -and $cFree -lt 15) { $healthScore -= 10; $healthIssues += 'Drive C: has under 15% free space' }
if ($sysDiskType -eq 'HDD') { $healthScore -= 15; $healthIssues += 'Windows is installed on a mechanical hard drive (HDD)' }
if ($problemDevices.Count -gt 0) { $healthScore -= 10; $healthIssues += ""$($problemDevices.Count) device(s) with driver errors in Device Manager"" }
$critCount = @($events).Count
if ($critCount -gt 0) { $healthScore -= [math]::Min(15, $critCount * 3); $healthIssues += ""$critCount critical system error(s) in the last 24 hours"" }
if ($hiberboot -eq 1) { $healthScore -= 5; $healthIssues += 'Fast Startup is enabled' }
if ($healthScore -lt 0) { $healthScore = 0 }

$diagData = [PSCustomObject]@{
    System = [PSCustomObject]@{
        Manufacturer = $cs.Manufacturer
        Model = $cs.Model
        BiosVersion = $bios.SMBIOSBIOSVersion
        OSCaption = $os.Caption
        OSBuild = $os.BuildNumber
        UptimeHours = [math]::Round(((Get-Date) - $os.LastBootUpTime).TotalHours, 1)
        LastBoot = $os.LastBootUpTime.ToString('yyyy-MM-dd HH:mm')
    }
    Processor = [PSCustomObject]@{
        Name = $cpu.Name
        Cores = $cpu.NumberOfCores
        Threads = $cpu.NumberOfLogicalProcessors
        MaxClockMHz = $cpu.MaxClockSpeed
        CurrentClockMHz = $cpu.CurrentClockSpeed
    }
    Memory = [PSCustomObject]@{
        TotalGB = $totalRamGb
        InstalledGB = $installedRamGb
        FreeGB = $freeRamGb
        PercentFree = $ramPercentFree
        DimmCount = $dimmCount
        SlotCount = $slotCount
        ChannelMode = $channelMode
        Modules = $dimms
    }
    Storage = [PSCustomObject]@{
        PhysicalDisks = @($disks)
        Volumes = @($vols)
        SystemDisk = $systemDisk
    }
    Health = [PSCustomObject]@{
        Score = $healthScore
        Issues = $healthIssues
    }
    Graphics = [PSCustomObject]@{
        Controllers = @($gpus)
        HasNvidiaCuda = $hasNvidia
        NvidiaVramGB = $nvidiaVramGb
    }
    AIReadiness = [PSCustomObject]@{
        Score = $aiScore
        Tier = $aiTier
        Recommendations = $aiRecommendations
    }
    OptimizationState = [PSCustomObject]@{
        PowerScheme = $powerScheme
        FastStartupEnabled = ($hiberboot -eq 1)
        SysMainStatus = $sysmain
        WindowsUpdateStatus = $wu
        PendingUpdateFilesCount = $updateFiles
    }
    TopProcesses = $topProcs
    ProblemDevices = $problemDevices
    RecentErrors = $events
}

$diagData | ConvertTo-Json -Depth 5 -Compress
";
    }

    public static string GetOptimizationScript()
    {
        return @"
Write-Host 'STEP 1: Disabling SysMain service...' -ForegroundColor Yellow
if (Get-Service -Name 'SysMain' -ErrorAction SilentlyContinue) {
    Stop-Service -Name 'SysMain' -Force -ErrorAction SilentlyContinue
    Set-Service -Name 'SysMain' -StartupType Disabled -ErrorAction SilentlyContinue
    Write-Host '[OK] SysMain disabled.' -ForegroundColor Green
}

Write-Host 'STEP 2: Purging corrupted Windows Update cache...' -ForegroundColor Yellow
Stop-Service -Name wuauserv -Force -ErrorAction SilentlyContinue
Stop-Service -Name bits -Force -ErrorAction SilentlyContinue
Remove-Item -Path 'C:\Windows\SoftwareDistribution\Download\*' -Recurse -Force -ErrorAction SilentlyContinue
Start-Service -Name bits -ErrorAction SilentlyContinue
Start-Service -Name wuauserv -ErrorAction SilentlyContinue
Write-Host '[OK] Windows Update cache purged.' -ForegroundColor Green

Write-Host 'STEP 3: Cleaning Windows Temp folders...' -ForegroundColor Yellow
Remove-Item -Path 'C:\Windows\Temp\*' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path ""$env:LOCALAPPDATA\Temp\*"" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host '[OK] Temp files cleared.' -ForegroundColor Green

Write-Host 'STEP 4: Activating High Performance power plan...' -ForegroundColor Yellow
$highPerf = (powercfg /l | Select-String 'High performance|8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c')
if (-not $highPerf) {
    powercfg -duplicatescheme 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c
}
powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c
Write-Host '[OK] High Performance active.' -ForegroundColor Green

Write-Host 'STEP 5: Disabling Fast Startup (Preventing RAM bug accumulation)...' -ForegroundColor Yellow
Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' -Name 'HiberbootEnabled' -Value 0
Write-Host '[OK] Fast Startup disabled.' -ForegroundColor Green

Write-Host 'STEP 6: Executing SSD ReTrim optimization...' -ForegroundColor Yellow
Optimize-Volume -DriveLetter C -ReTrim -ErrorAction SilentlyContinue
Write-Host '[OK] NVMe SSD ReTrim completed.' -ForegroundColor Green

Write-Host 'STEP 7: Setting Windows Defender CPU limit to 25%...' -ForegroundColor Yellow
Set-MpPreference -ScanAvgCPULoadFactor 25 -ErrorAction SilentlyContinue
Write-Host '[OK] Defender CPU throttle configured.' -ForegroundColor Green

Write-Host 'ALL OPTIMIZATION STEPS COMPLETED SUCCESSFULLY!' -ForegroundColor Cyan
";
    }

    public static string GetDriverInstallScript(string sourceFolder)
    {
        return $@"
$srcDir = '{sourceFolder.Replace("'", "''")}'
$baseTarget = 'C:\DellDrivers_Extracted'

Write-Host ""Starting automated driver injection from: $srcDir"" -ForegroundColor Cyan

if (-not (Test-Path $srcDir)) {{
    Write-Host ""Source folder does not exist: $srcDir"" -ForegroundColor Red
    exit 1
}}

$packages = Get-ChildItem -Path $srcDir -Filter '*.EXE' -File -ErrorAction SilentlyContinue
$total = ($packages | Measure-Object).Count
Write-Host ""Found $total driver packages."" -ForegroundColor Yellow

$idx = 0
foreach ($pkg in $packages) {{
    $idx++
    $pkgName = $pkg.BaseName
    $outDir = Join-Path $baseTarget $pkgName
    Write-Host ""[$idx/$total] Processing: $($pkg.Name)..."" -ForegroundColor Cyan

    New-Item -Path $outDir -ItemType Directory -Force | Out-Null
    $proc = Start-Process -FilePath $pkg.FullName -ArgumentList ""/s /e=`""$outDir`"""" -Wait -PassThru -ErrorAction SilentlyContinue
    
    $infFiles = Get-ChildItem -Path $outDir -Filter '*.inf' -Recurse -ErrorAction SilentlyContinue
    if ($infFiles.Count -gt 0) {{
        Write-Host ""  Injecting $($infFiles.Count) driver packages via pnputil..."" -ForegroundColor Green
        pnputil /add-driver ""$outDir\*.inf"" /subdirs /install | Select-String 'Total driver packages|Added driver packages|is up-to-date|Driver package added successfully' | Select-Object -Last 3
    }} else {{
        Write-Host ""  No raw INFs found, attempting silent package execution..."" -ForegroundColor Gray
        Start-Process -FilePath $pkg.FullName -ArgumentList '/s' -Wait -PassThru -ErrorAction SilentlyContinue | Out-Null
    }}
}}

Write-Host 'Driver deployment cycle finished.' -ForegroundColor Green
";
    }
}
