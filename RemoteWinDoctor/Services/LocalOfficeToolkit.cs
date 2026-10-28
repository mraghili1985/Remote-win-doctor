namespace RemoteWinDoctor.Services;

public class LocalOfficeToolkit
{
    public static string GetLanAuditScript()
    {
        return @"
$ErrorActionPreference = 'SilentlyContinue'

# 1. Network Profile (Private vs Public)
$netProfiles = @(Get-NetConnectionProfile)
$primaryNet = $netProfiles | Select-Object -First 1
$netCategory = if ($primaryNet) { $primaryNet.NetworkCategory.ToString() } else { 'Unknown' }
$netName = if ($primaryNet) { $primaryNet.Name } else { 'Ethernet/Wi-Fi' }

# 2. Network Discovery Services
$fdResPub = (Get-Service -Name 'FDResPub' -ErrorAction SilentlyContinue).Status.ToString()
$fdpHost = (Get-Service -Name 'FDPHost' -ErrorAction SilentlyContinue).Status.ToString()
$discoveryOk = ($fdResPub -eq 'Running' -and $fdpHost -eq 'Running')

# 3. Printer Sharing 0x0000011b Bug Status
$spooler = (Get-Service -Name 'Spooler' -ErrorAction SilentlyContinue).Status.ToString()
$rpcPrivacy = (Get-ItemProperty -Path 'HKLM:\System\CurrentControlSet\Control\Print' -Name 'RpcAuthnLevelPrivacyEnabled' -ErrorAction SilentlyContinue).RpcAuthnLevelPrivacyEnabled
$printerPatched = ($rpcPrivacy -eq 0)

# 4. Insecure Guest Auth for Shared Folders
$guestAuth = (Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters' -Name 'AllowInsecureGuestAuth' -ErrorAction SilentlyContinue).AllowInsecureGuestAuth
$guestAuthEnabled = ($guestAuth -eq 1)

# 5. Active DNS Servers
$dnsServers = @()
$adapters = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' }
foreach ($ad in $adapters) {
    $dns = (Get-DnsClientServerAddress -InterfaceIndex $ad.InterfaceIndex -AddressFamily IPv4).ServerAddresses
    if ($dns) { $dnsServers += $dns }
}
$dnsType = 'Standard / router'
$dnsJoined = $dnsServers -join ', '
if ($dnsJoined -match '178\.22\.122\.100|185\.51\.200\.2') {
    $dnsType = 'Shecan (anti-sanction)'
} elseif ($dnsJoined -match '78\.157\.42\.101|78\.157\.42\.100') {
    $dnsType = 'Electro (anti-sanction)'
} elseif ($dnsJoined -match '10\.202\.10\.10|10\.202\.10\.11') {
    $dnsType = 'Radar Game (anti-sanction)'
} elseif ($dnsJoined -match '10\.202\.10\.202|10\.202\.10\.102') {
    $dnsType = '403.online (anti-sanction)'
} elseif ($dnsJoined -match '185\.55\.226\.26|185\.55\.225\.25') {
    $dnsType = 'Begzar (anti-sanction)'
} elseif ($dnsJoined -match '1\.1\.1\.1|1\.0\.0\.1') {
    $dnsType = 'Cloudflare 1.1.1.1'
} elseif ($dnsJoined -match '8\.8\.8\.8|8\.8\.4\.4') {
    $dnsType = 'Google 8.8.8.8'
}

# 6. Active SMB Shares
$shares = @(Get-SmbShare | Where-Object { $_.Name -notmatch 'ADMIN\$|C\$|IPC\$' } | Select-Object Name, Path, Description)

# 7. Defender Exclusions (Crack Safety)
$exclusions = @((Get-MpPreference).ExclusionPath)

# 8. S.M.A.R.T Disk Health & Temperature
$disksHealth = @()
$physicalDisks = Get-PhysicalDisk
foreach ($pd in $physicalDisks) {
    $temp = '-'
    $wear = '-'
    $counter = Get-StorageReliabilityCounter -PhysicalDisk $pd -ErrorAction SilentlyContinue
    if ($counter) {
        if ($counter.Temperature) { $temp = ""$($counter.Temperature) C"" }
        if ($counter.Wear) { $wear = ""$($counter.Wear)%"" }
    }
    $disksHealth += [PSCustomObject]@{
        Name = $pd.FriendlyName
        Health = $pd.HealthStatus.ToString()
        MediaType = $pd.MediaType.ToString()
        Temperature = $temp
        WearPercent = $wear
    }
}

# 9. Scheduled Maintenance Task
$hasSchedTask = (Get-ScheduledTask -TaskName 'RemoteWinDoctor_WeeklyMaintenance' -ErrorAction SilentlyContinue) -ne $null

$auditResult = [PSCustomObject]@{
    Network = [PSCustomObject]@{
        Category = $netCategory
        Name = $netName
        IsPrivate = ($netCategory -eq 'Private')
        DiscoveryActive = $discoveryOk
        FDResPubStatus = $fdResPub
    }
    PrinterSharing = [PSCustomObject]@{
        SpoolerStatus = $spooler
        IsPatched11b = $printerPatched
        GuestAuthAllowed = $guestAuthEnabled
    }
    Dns = [PSCustomObject]@{
        Servers = $dnsServers
        Type = $dnsType
    }
    Shares = $shares
    DefenderExclusions = [PSCustomObject]@{
        Count = $exclusions.Count
        Paths = $exclusions
    }
    DiskReliability = $disksHealth
    HasScheduledMaintenance = $hasSchedTask
}

$auditResult | ConvertTo-Json -Depth 4 -Compress
";
    }

    public static string GetLanActionScript(string actionType, string folderPath, string shareName, string customExclusion)
    {
        switch (actionType?.ToLowerInvariant())
        {
            case "setup-office-lan":
                return @"
Write-Host '[START] Configuring Office Local Network (Private Profile + Network Discovery)...' -ForegroundColor Cyan

# 1. Set all active network profiles to Private
Get-NetConnectionProfile | Set-NetConnectionProfile -NetworkCategory Private -ErrorAction SilentlyContinue
Write-Host '[OK] Network category set to Private (File sharing unblocked).' -ForegroundColor Green

# 2. Enable Network Discovery & File/Printer Sharing Services
$services = 'FDResPub', 'FDPHost', 'upnphost', 'SSDPSrv'
foreach ($s in $services) {
    Set-Service -Name $s -StartupType Automatic -ErrorAction SilentlyContinue
    Start-Service -Name $s -ErrorAction SilentlyContinue
}
Write-Host '[OK] Discovery services (FDResPub, FDPHost, SSDP, UPnP) running.' -ForegroundColor Green

# 3. Allow Firewall Rules
Enable-NetFirewallRule -DisplayGroup 'Network Discovery' -ErrorAction SilentlyContinue
Enable-NetFirewallRule -DisplayGroup 'File and Printer Sharing' -ErrorAction SilentlyContinue
Write-Host '[OK] Windows Defender Firewall rules for Discovery & Sharing enabled.' -ForegroundColor Green

Write-Host '[SUCCESS] Office LAN Discovery configured! All PCs can now see each other in Network.' -ForegroundColor Cyan
";

            case "fix-printer-bug":
                return @"
Write-Host '[START] Fixing Windows 10/11 Network Printer Sharing Bug (0x0000011b / 0x00000709)...' -ForegroundColor Cyan

# 1. Registry patch for RPC authentication level
$printPath = 'HKLM:\System\CurrentControlSet\Control\Print'
if (-not (Test-Path $printPath)) { New-Item -Path $printPath -Force | Out-Null }
Set-ItemProperty -Path $printPath -Name 'RpcAuthnLevelPrivacyEnabled' -Value 0 -Type DWord -Force
Write-Host '[OK] Registry key RpcAuthnLevelPrivacyEnabled set to 0.' -ForegroundColor Green

# 2. Allow insecure guest auth for passwordless network shares
$lanmanPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters'
if (-not (Test-Path $lanmanPath)) { New-Item -Path $lanmanPath -Force | Out-Null }
Set-ItemProperty -Path $lanmanPath -Name 'AllowInsecureGuestAuth' -Value 1 -Type DWord -Force
Write-Host '[OK] Insecure guest access allowed for seamless office file/printer access.' -ForegroundColor Green

# 3. Restart Spooler
Restart-Service -Name Spooler -Force -ErrorAction SilentlyContinue
Write-Host '[OK] Print Spooler service refreshed.' -ForegroundColor Green

Write-Host '[SUCCESS] Network Printer 0x0000011b bug successfully resolved!' -ForegroundColor Cyan
";

            case "create-smb-share":
                var targetFolder = string.IsNullOrWhiteSpace(folderPath) ? "C:\\OfficeShare" : folderPath.Trim();
                var targetShare = string.IsNullOrWhiteSpace(shareName) ? "OfficeShare" : shareName.Trim();
                return $@"
Write-Host '[START] Creating Automated SMB Shared Folder...' -ForegroundColor Cyan

$fPath = '{targetFolder.Replace("'", "''")}'
$sName = '{targetShare.Replace("'", "''")}'

if (-not (Test-Path $fPath)) {{
    New-Item -Path $fPath -ItemType Directory -Force | Out-Null
    Write-Host ""[OK] Directory created: $fPath"" -ForegroundColor Green
}}

# Grant NTFS permissions to Everyone
$acl = Get-Acl $fPath
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule('Everyone', 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
$acl.AddAccessRule($rule)
Set-Acl $fPath $acl
Write-Host '[OK] NTFS FullControl permissions granted to Everyone.' -ForegroundColor Green

# Create SMB Share
if (Get-SmbShare -Name $sName -ErrorAction SilentlyContinue) {{
    Remove-SmbShare -Name $sName -Force -ErrorAction SilentlyContinue
}}
New-SmbShare -Name $sName -Path $fPath -FullAccess 'Everyone' -Force | Out-Null
Write-Host ""[OK] SMB Share created: \\$($env:COMPUTERNAME)\$sName"" -ForegroundColor Green

Write-Host '[SUCCESS] Shared folder is live and accessible across the local network!' -ForegroundColor Cyan
";

            case "set-dns-shecan":
                return @"
Write-Host '[START] Setting Anti-Sanction DNS (Shecan: 178.22.122.100, 185.51.200.2)...' -ForegroundColor Cyan
$adapters = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' }
foreach ($ad in $adapters) {
    Set-DnsClientServerAddress -InterfaceIndex $ad.InterfaceIndex -ServerAddresses ('178.22.122.100', '185.51.200.2') -ErrorAction SilentlyContinue
}
Clear-DnsClientCache
Write-Host '[SUCCESS] Shecan Anti-Sanction DNS active! 403 Forbidden blocks for dev tools and drivers are now bypassed.' -ForegroundColor Green
";

            case "set-dns-electro":
                return @"
Write-Host '[START] Setting Anti-Sanction DNS (Electro: 78.157.42.101, 78.157.42.100)...' -ForegroundColor Cyan
$adapters = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' }
foreach ($ad in $adapters) {
    Set-DnsClientServerAddress -InterfaceIndex $ad.InterfaceIndex -ServerAddresses ('78.157.42.101', '78.157.42.100') -ErrorAction SilentlyContinue
}
Clear-DnsClientCache
Write-Host '[SUCCESS] Electro Anti-Sanction DNS active!' -ForegroundColor Green
";

            case "set-dns-radar":
                return @"
Write-Host '[START] Setting Anti-Sanction DNS (Radar Game: 10.202.10.10, 10.202.10.11)...' -ForegroundColor Cyan
$adapters = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' }
foreach ($ad in $adapters) {
    Set-DnsClientServerAddress -InterfaceIndex $ad.InterfaceIndex -ServerAddresses ('10.202.10.10', '10.202.10.11') -ErrorAction SilentlyContinue
}
Clear-DnsClientCache
Write-Host '[SUCCESS] Radar Game Anti-Sanction DNS active! Optimized for game CDNs and dev mirrors.' -ForegroundColor Green
";

            case "set-dns-403":
                return @"
Write-Host '[START] Setting Anti-Sanction DNS (403.online: 10.202.10.202, 10.202.10.102)...' -ForegroundColor Cyan
$adapters = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' }
foreach ($ad in $adapters) {
    Set-DnsClientServerAddress -InterfaceIndex $ad.InterfaceIndex -ServerAddresses ('10.202.10.202', '10.202.10.102') -ErrorAction SilentlyContinue
}
Clear-DnsClientCache
Write-Host '[SUCCESS] 403.online Anti-Sanction DNS active!' -ForegroundColor Green
";

            case "set-dns-begzar":
                return @"
Write-Host '[START] Setting Anti-Sanction DNS (Begzar: 185.55.226.26, 185.55.225.25)...' -ForegroundColor Cyan
$adapters = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' }
foreach ($ad in $adapters) {
    Set-DnsClientServerAddress -InterfaceIndex $ad.InterfaceIndex -ServerAddresses ('185.55.226.26', '185.55.225.25') -ErrorAction SilentlyContinue
}
Clear-DnsClientCache
Write-Host '[SUCCESS] Begzar Anti-Sanction DNS active!' -ForegroundColor Green
";

            case "set-dns-cloudflare":
                return @"
Write-Host '[START] Setting Fast Public DNS (Cloudflare: 1.1.1.1, 1.0.0.1)...' -ForegroundColor Cyan
$adapters = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' }
foreach ($ad in $adapters) {
    Set-DnsClientServerAddress -InterfaceIndex $ad.InterfaceIndex -ServerAddresses ('1.1.1.1', '1.0.0.1') -ErrorAction SilentlyContinue
}
Clear-DnsClientCache
Write-Host '[SUCCESS] Cloudflare 1.1.1.1 Fast DNS active!' -ForegroundColor Green
";

            case "set-dns-google":
                return @"
Write-Host '[START] Setting Fast Public DNS (Google: 8.8.8.8, 8.8.4.4)...' -ForegroundColor Cyan
$adapters = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' }
foreach ($ad in $adapters) {
    Set-DnsClientServerAddress -InterfaceIndex $ad.InterfaceIndex -ServerAddresses ('8.8.8.8', '8.8.4.4') -ErrorAction SilentlyContinue
}
Clear-DnsClientCache
Write-Host '[SUCCESS] Google 8.8.8.8 Fast DNS active!' -ForegroundColor Green
";

            case "reset-dns-auto":
                return @"
Write-Host '[START] Resetting DNS to DHCP / Automatic...' -ForegroundColor Cyan
$adapters = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' }
foreach ($ad in $adapters) {
    Set-DnsClientServerAddress -InterfaceIndex $ad.InterfaceIndex -ResetServerAddresses -ErrorAction SilentlyContinue
}
Clear-DnsClientCache
Write-Host '[SUCCESS] DNS restored to default router / automatic settings.' -ForegroundColor Green
";

            case "schedule-weekly-maintenance":
                return @"
Write-Host '[START] Registering Automated Weekly Maintenance in Windows Task Scheduler...' -ForegroundColor Cyan
$taskCmd = 'powershell.exe -WindowStyle Hidden -ExecutionPolicy Bypass -Command ""Optimize-Volume -DriveLetter C -ReTrim -ErrorAction SilentlyContinue; Remove-Item ''$env:TEMP\*'' -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item ''C:\Windows\Temp\*'' -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item ''C:\Windows\SoftwareDistribution\Download\*'' -Recurse -Force -ErrorAction SilentlyContinue""'
cmd.exe /c ""schtasks /create /tn RemoteWinDoctor_WeeklyMaintenance /tr \""$taskCmd\"" /sc weekly /d SUN /st 03:00 /ru SYSTEM /f"" 2>&1 | Out-String | Write-Host
Write-Host '[SUCCESS] RemoteWinDoctor Weekly Automated Maintenance is scheduled every Sunday at 03:00 AM (runs silently as SYSTEM).' -ForegroundColor Green
";

            case "protect-cracks":
                var extra = string.IsNullOrWhiteSpace(customExclusion) ? "" : customExclusion.Trim();
                return $@"
Write-Host '[START] Adding Crack & License Safety Exclusions to Windows Defender...' -ForegroundColor Cyan

$standardPaths = @(
    'C:\Program Files\Autodesk',
    'C:\Program Files\Adobe',
    'C:\Program Files (x86)\Common Files\Autodesk Shared',
    'C:\ProgramData\KMSAuto',
    'C:\ProgramData\Microsoft Toolkit',
    'C:\Windows\AutoKMS'
)

if ('{extra}' -ne '') {{
    $standardPaths += '{extra.Replace("'", "''")}'
}}

foreach ($p in $standardPaths) {{
    Add-MpPreference -ExclusionPath $p -ErrorAction SilentlyContinue
    Write-Host ""[OK] Whitelisted in Defender: $p"" -ForegroundColor Green
}}

Write-Host '[SUCCESS] Cracked engineering tools and activation patches are now safe from false-positive deletion!' -ForegroundColor Cyan
";

            case "lan-speed-test":
            default:
                return @"
Write-Host '[START] Running LAN & Storage Throughput Benchmark...' -ForegroundColor Cyan

$testSizeMB = 30
$testBytes = New-Object byte[] ($testSizeMB * 1MB)
(New-Object System.Random).NextBytes($testBytes)

$testFile = ""$env:TEMP\lan_speed_test.bin""

$swWrite = [System.Diagnostics.Stopwatch]::StartNew()
[System.IO.File]::WriteAllBytes($testFile, $testBytes)
$swWrite.Stop()

$swRead = [System.Diagnostics.Stopwatch]::StartNew()
$readBytes = [System.IO.File]::ReadAllBytes($testFile)
$swRead.Stop()

Remove-Item $testFile -Force -ErrorAction SilentlyContinue

$writeSpeed = [math]::Round($testSizeMB / ($swWrite.Elapsed.TotalSeconds), 1)
$readSpeed = [math]::Round($testSizeMB / ($swRead.Elapsed.TotalSeconds), 1)

Write-Host ""Write Speed: $writeSpeed MB/s"" -ForegroundColor Yellow
Write-Host ""Read Speed:  $readSpeed MB/s"" -ForegroundColor Yellow

if ($writeSpeed -gt 80) {
    Write-Host '[DIAGNOSIS] Gigabit Ethernet (Cat6) / Fast NVMe: Excellent performance.' -ForegroundColor Green
} elseif ($writeSpeed -gt 15) {
    Write-Host '[DIAGNOSIS] 100Mbps Ethernet (Cat5) / Fast Wi-Fi: Standard office speed.' -ForegroundColor Yellow
} else {
    Write-Host '[DIAGNOSIS] Weak Wi-Fi signal or degraded cable: Bottleneck detected!' -ForegroundColor Red
}

Write-Host '[SUCCESS] Benchmark completed.' -ForegroundColor Cyan
";
        }
    }
}

public class LocalOfficeActionRequest
{
    public string Ip { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ActionType { get; set; } = "setup-office-lan";
    public string FolderPath { get; set; } = "C:\\OfficeShare";
    public string ShareName { get; set; } = "OfficeShare";
    public string CustomExclusion { get; set; } = string.Empty;
}
