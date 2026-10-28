using System.Text.Json;

namespace RemoteWinDoctor.Services;

public class WizardHostInfo
{
    public string Host { get; set; } = "";
    public string Workgroup { get; set; } = "";
    public List<WizardAdapter> Adapters { get; set; } = new();
    public List<string> Vpns { get; set; } = new();
    public List<WizardPrinter> Printers { get; set; } = new();
    public List<WizardShare> Shares { get; set; } = new();
    public int PublicSharingRules { get; set; }
    public bool IsLaptop { get; set; }
}

public class WizardAdapter
{
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Category { get; set; } = "";
    public bool Dhcp { get; set; }
    public string Mac { get; set; } = "";
    public string Gateway { get; set; } = "";
    public int Prefix { get; set; }
    public List<string> Dns { get; set; } = new();
    public bool Wireless { get; set; }
}

public class WizardPrinter
{
    public string Name { get; set; } = "";
    public bool Shared { get; set; }
    public string ShareName { get; set; } = "";
    public string Status { get; set; } = "";
}

public class WizardShare
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

/// <summary>One verified step result. Status is ok / warn / fail / info.</summary>
public record WizardStep(string Status, string Title, string Detail);

/// <summary>
/// Office network setup, tested against a real two-PC workgroup. Sharing is always
/// password-protected (an account on the host); every change is re-read after it is applied
/// and reported as RWD|status|title|detail, because some (e.g. disabling Public firewall
/// rules) can silently not take effect on the first attempt.
/// </summary>
public static class OfficeNetworkWizard
{
    // Locale-independent firewall group IDs ("File and Printer Sharing", "Network Discovery")
    private const string FpsGroup = "@FirewallAPI.dll,-28502";
    private const string NdGroup = "@FirewallAPI.dll,-32752";

    private const string ReportFn = @"
function Report($status, $title, $detail) { Write-Output ""RWD|$status|$title|$detail"" }
";

    private static string Q(string s) => s.Replace("'", "''");

    public static string GetHostCheckScript() => @"
$ErrorActionPreference = 'SilentlyContinue'
$adapters = foreach ($a in Get-NetAdapter -Physical | Where-Object Status -eq 'Up') {
    $ip = (Get-NetIPAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '169.254*' } | Select-Object -First 1).IPAddress
    $addr = Get-NetIPAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '169.254*' } | Select-Object -First 1
    $cat = (Get-NetConnectionProfile -InterfaceIndex $a.ifIndex).NetworkCategory
    $gw = (Get-NetRoute -InterfaceIndex $a.ifIndex -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric | Select-Object -First 1).NextHop
    $dhcp = ""$((Get-NetIPInterface -InterfaceIndex $a.ifIndex -AddressFamily IPv4).Dhcp)"" -eq 'Enabled'
    $dns = @((Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4).ServerAddresses)
    $wireless = ($a.NdisPhysicalMedium -eq 9) -or ($a.InterfaceDescription -match 'Wi-?Fi|Wireless|WLAN|802\.11')
    if ($ip) { [PSCustomObject]@{ Name = $a.Name; Ip = $ip; Category = ""$cat""; Dhcp = $dhcp; Mac = ""$($a.MacAddress)"".Replace('-', ':'); Gateway = ""$gw""; Prefix = [int]$addr.PrefixLength; Dns = $dns; Wireless = [bool]$wireless } }
}
$vpns = @(Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and $_.InterfaceDescription -match 'VPN|TAP|WireGuard|OpenVPN|ZeroTier|Tailscale|Wintun|Fortinet|Cisco AnyConnect' } | ForEach-Object { $_.Name })
$printers = @(Get-Printer | Where-Object {
    $_.Type -eq 'Local' -and $_.DriverName -notmatch 'PDF|XPS|OneNote|Fax|AnyDesk|Send To' -and $_.PortName -notmatch 'PORTPROMPT|SHRFAX|nul:|\.pdf|Documents'
} | ForEach-Object { [PSCustomObject]@{ Name = $_.Name; Shared = [bool]$_.Shared; ShareName = ""$($_.ShareName)""; Status = ""$($_.PrinterStatus)"" } })
$shares = @(Get-SmbShare | Where-Object { -not $_.Special -and $_.Name -notmatch '\$$' -and $_.ShareType -eq 'FileSystemDirectory' } | ForEach-Object { [PSCustomObject]@{ Name = $_.Name; Path = $_.Path } })
$publicRules = @(Get-NetFirewallRule -Group '" + FpsGroup + @"' | Where-Object { $_.Direction -eq 'Inbound' -and $_.Enabled -eq 'True' -and $_.Profile -match 'Public' }).Count
[PSCustomObject]@{
    Host = $env:COMPUTERNAME
    Workgroup = (Get-CimInstance Win32_ComputerSystem).Workgroup
    Adapters = @($adapters); Vpns = $vpns; Printers = $printers; Shares = $shares
    PublicSharingRules = $publicRules
    IsLaptop = [bool](Get-CimInstance Win32_Battery) -or [bool](@((Get-CimInstance Win32_SystemEnclosure).ChassisTypes) | Where-Object { $_ -in 8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32 })
} | ConvertTo-Json -Depth 4 -Compress
";

    /// <param name="printerName">Printer to share, or empty for none.</param>
    /// <param name="folderPath">Folder to share, or empty for none.</param>
    /// <param name="account">Account that gets access; clients sign in with it.</param>
    public static string GetHostSetupScript(string printerName, string printerShareName, string folderPath, string folderShareName, string account) => ReportFn + $@"
$ErrorActionPreference = 'SilentlyContinue'
$printerName = '{Q(printerName)}'; $printerShare = '{Q(printerShareName)}'
$folderPath = '{Q(folderPath)}'; $folderShare = '{Q(folderShareName)}'
$account = '{Q(account)}'

# 1. Private network profile on the physical LAN adapters only (VPN adapters are left alone)
foreach ($a in Get-NetAdapter -Physical | Where-Object Status -eq 'Up') {{
    $p = Get-NetConnectionProfile -InterfaceIndex $a.ifIndex
    if (-not $p) {{ continue }}
    if ($p.NetworkCategory -eq 'DomainAuthenticated') {{ Report info ""Network '$($a.Name)'"" 'Domain network; profile left unchanged.'; continue }}
    if ($p.NetworkCategory -ne 'Private') {{ Set-NetConnectionProfile -InterfaceIndex $a.ifIndex -NetworkCategory Private }}
    if ((Get-NetConnectionProfile -InterfaceIndex $a.ifIndex).NetworkCategory -eq 'Private') {{ Report ok ""Network '$($a.Name)' is Private"" 'Other PCs on this network are trusted for sharing.' }}
    else {{ Report fail ""Network '$($a.Name)' is still Public"" 'Could not change it. In Settings > Network, set this network to Private.' }}
}}

# 2. Firewall: sharing + discovery allowed on Private, closed on Public (re-applied once if it did not stick)
function Get-FwState {{
    $r = Get-NetFirewallRule -Group '{FpsGroup}','{NdGroup}' | Where-Object Direction -eq 'Inbound'
    [PSCustomObject]@{{
        PrivateOff = @($r | Where-Object {{ $_.Profile -match 'Private' -and $_.Enabled -ne 'True' }}).Count
        PublicOn = @($r | Where-Object {{ $_.Profile -eq 'Public' -and $_.Enabled -eq 'True' }}).Count
    }}
}}
for ($try = 0; $try -lt 2; $try++) {{
    $rules = Get-NetFirewallRule -Group '{FpsGroup}','{NdGroup}'
    $rules | Where-Object {{ $_.Profile -match 'Private' }} | Enable-NetFirewallRule
    $rules | Where-Object {{ $_.Profile -eq 'Public' }} | Disable-NetFirewallRule
    $fw = Get-FwState
    if ($fw.PrivateOff -eq 0 -and $fw.PublicOn -eq 0) {{ break }}
    Start-Sleep -Seconds 2
}}
if ($fw.PrivateOff -eq 0) {{ Report ok 'Firewall allows sharing on Private networks' 'File and Printer Sharing and Network Discovery are open for this network only.' }}
else {{ Report fail 'Firewall still blocks sharing' ""$($fw.PrivateOff) sharing rule(s) could not be enabled. A security product may be managing the firewall."" }}
if ($fw.PublicOn -eq 0) {{ Report ok 'Sharing is closed on public networks' 'On public Wi-Fi (cafe, hotel) this PC''s files and printers stay hidden.' }}
else {{ Report warn 'Sharing is still open on public networks' ""$($fw.PublicOn) rule(s) stayed enabled for Public. Turn off File and Printer Sharing for Public in Windows Firewall."" }}

# 3. Services that make the PC visible and serve shares; Automatic so it survives restarts
$bad = @()
foreach ($s in 'LanmanServer','FDResPub','FDPHost','SSDPSrv','upnphost') {{
    Set-Service $s -StartupType Automatic; Start-Service $s
    if ((Get-Service $s).Status -ne 'Running') {{ $bad += $s }}
}}
if ($bad.Count -eq 0) {{ Report ok 'Sharing and discovery services are running' 'They also start automatically after a restart.' }}
else {{ Report fail 'Some services did not start' ""Not running: $($bad -join ', ')."" }}

# 4. Printer
if ($printerName) {{
    $pr = Get-Printer -Name $printerName
    if (-not $pr) {{ Report fail ""Printer '$printerName' not found"" 'Install the printer and its driver on this PC first.' }}
    else {{
        # 0x0000011b: clients on newer updates fail to connect unless the host allows this auth level
        Set-ItemProperty 'HKLM:\System\CurrentControlSet\Control\Print' -Name RpcAuthnLevelPrivacyEnabled -Value 0 -Type DWord
        Set-Printer -Name $printerName -Shared $true -ShareName $printerShare
        Restart-Service Spooler -Force
        $pr = Get-Printer -Name $printerName
        if ($pr.Shared) {{ Report ok ""Printer shared as '$($pr.ShareName)'"" 'Includes the fix for connection error 0x0000011b.' }}
        else {{ Report fail ""Could not share '$printerName'"" 'Open Printers & scanners > the printer > Printer properties > Sharing, and share it there.' }}
        if (""$($pr.PrinterStatus)"" -notin @('Normal','Idle','0','3')) {{ Report warn 'Printer is not ready' ""Status: $($pr.PrinterStatus). Check that it is switched on, connected and has paper."" }}
    }}
}}

# 5. Folder: an existing share name is never removed or repointed
if ($folderPath) {{
    if (-not (Test-Path -LiteralPath $folderPath)) {{ New-Item -ItemType Directory -Path $folderPath -Force | Out-Null }}
    icacls ""$folderPath"" /grant ""$($account):(OI)(CI)M"" | Out-Null
    $existing = Get-SmbShare -Name $folderShare
    if ($existing -and $existing.Path -ne $folderPath) {{
        Report fail ""Share name '$folderShare' is already used"" ""It points to $($existing.Path). Pick another share name; the existing share was not changed.""
    }} else {{
        if (-not $existing) {{ New-SmbShare -Name $folderShare -Path $folderPath -FullAccess $account -Description 'Office shared folder' | Out-Null }}
        else {{ Grant-SmbShareAccess -Name $folderShare -AccountName $account -AccessRight Full -Force | Out-Null }}
        $acc = Get-SmbShareAccess -Name $folderShare | Where-Object {{ $_.AccountName -match [regex]::Escape(($account -split '\\')[-1]) -and $_.AccessControlType -eq 'Allow' }}
        if ($acc) {{ Report ok ""Folder shared as '$folderShare'"" ""$folderPath. Others sign in as $account."" }}
        else {{ Report fail ""Could not share '$folderPath'"" 'The share or its permissions could not be created.' }}
    }}
}}

# 6. File sharing port
if (Get-NetTCPConnection -LocalPort 445 -State Listen) {{ Report ok 'Port 445 is listening' 'Other PCs can reach the shares.' }}
else {{ Report fail 'Port 445 is not listening' 'The Server service is not accepting connections.' }}
";

    /// <summary>
    /// Runs on THIS PC only (not over WinRM): saved credentials and printer connections are
    /// per interactive user, and over WinRM they would land in the remote login account.
    /// Reads the host password from $env:RWD_SHARE_PASS.
    /// </summary>
    public static string GetClientSetupScript(string hostIp, string hostName, string user, string printerShare, string folderShare) => ReportFn + $@"
$ErrorActionPreference = 'SilentlyContinue'
$hostIp = '{Q(hostIp)}'; $hostName = '{Q(hostName)}'; $user = '{Q(user)}'
$printerShare = '{Q(printerShare)}'; $folderShare = '{Q(folderShare)}'
$pass = $env:RWD_SHARE_PASS; Remove-Item Env:RWD_SHARE_PASS
if ($user -notmatch '\\') {{ $user = ""$hostName\$user"" }}

# 1. This PC's network profile
foreach ($a in Get-NetAdapter -Physical | Where-Object Status -eq 'Up') {{
    $p = Get-NetConnectionProfile -InterfaceIndex $a.ifIndex
    if (-not $p -or $p.NetworkCategory -ne 'Public') {{ continue }}
    Set-NetConnectionProfile -InterfaceIndex $a.ifIndex -NetworkCategory Private
    if ((Get-NetConnectionProfile -InterfaceIndex $a.ifIndex).NetworkCategory -eq 'Private') {{ Report ok ""Network '$($a.Name)' set to Private"" '' }}
    else {{ Report warn ""Network '$($a.Name)' is Public on this PC"" 'Run RemoteWinDoctor as administrator, or set it to Private in Settings > Network.' }}
}}

# 2. Can we reach the host at all?
$tcp = New-Object Net.Sockets.TcpClient
$reach = $tcp.ConnectAsync($hostIp, 445).Wait(3000) -and $tcp.Connected; $tcp.Close()
if (-not $reach) {{ Report fail ""Cannot reach $hostIp on port 445"" 'Run the host setup first, and check both PCs are on the same network.'; return }}
Report ok ""Host $hostIp is reachable"" ''

# 3. Remember the host's credentials for this Windows user (shown in Credential Manager)
foreach ($t in @($hostIp, $hostName) | Where-Object {{ $_ }}) {{ cmdkey /add:$t /user:$user /pass:$pass | Out-Null }}
if ((cmdkey /list) -match [regex]::Escape($hostIp)) {{ Report ok 'Host credentials saved' ""Signs in as $user automatically; remove them any time in Credential Manager."" }}
else {{ Report fail 'Could not save the host credentials' 'Windows will ask for the password each time.' }}

# 4. Shared folder: write and delete a test file
if ($folderShare) {{
    $unc = ""\\$hostIp\$folderShare""
    $test = Join-Path $unc ("".rwd-test-"" + [guid]::NewGuid().ToString('N') + "".tmp"")
    try {{
        Set-Content -LiteralPath $test -Value 'test' -ErrorAction Stop
        Remove-Item -LiteralPath $test -Force
        Report ok ""Shared folder works: $unc"" 'Read and write access confirmed with a test file.'
    }} catch {{
        $m = $_.Exception.Message
        if ($m -match 'password|credential|logon|1326|denied') {{ Report fail ""Access to $unc was refused"" ""Wrong username/password, or that account has no password (Windows blocks network sign-in for accounts without one). $m"" }}
        else {{ Report fail ""Cannot open $unc"" $m }}
    }}
}}

# 5. Printer connection (driver comes from the host)
if ($printerShare) {{
    $conn = ""\\$hostIp\$printerShare""
    if (Get-Printer -Name $conn) {{ Report ok ""Printer already connected: $conn"" '' }}
    else {{
        try {{
            Add-Printer -ConnectionName $conn -ErrorAction Stop
            if (Get-Printer -Name $conn) {{ Report ok ""Printer connected: $conn"" 'It now appears in Printers & scanners on this PC.' }}
            else {{ Report fail 'Printer did not appear after connecting' $conn }}
        }} catch {{
            $m = $_.Exception.Message
            if ($m -match 'administrator|0x00000bcb|0x0000007e|driver|0x00000709') {{ Report warn 'Printer needs a driver on this PC' ""Windows only lets administrators install printer drivers from another PC. Run RemoteWinDoctor as administrator and retry, or install the printer's driver on this PC first. ($m)"" }}
            else {{ Report fail ""Could not connect $conn"" $m }}
        }}
    }}
}}

# 6. Name lookup (paths by IP always work; by name needs local name resolution)
$byName = $null
try {{ $byName = [Net.Dns]::GetHostAddresses($hostName) | Where-Object {{ ""$_"" -eq $hostIp }} }} catch {{ }}
if ($byName) {{ Report ok ""\\$hostName also works"" '' }}
else {{
    $vpn = @(Get-NetAdapter | Where-Object {{ $_.Status -eq 'Up' -and $_.InterfaceDescription -match 'VPN|TAP|WireGuard|OpenVPN|Wintun|Tailscale' }} | ForEach-Object {{ $_.Name }})
    $why = if ($vpn) {{ ""A VPN is active ($($vpn -join ', ')) and is answering name lookups."" }} else {{ 'Local name lookup is not answering.' }}
    Report warn ""Use \\$hostIp instead of \\$hostName"" ""$why Paths by IP address work normally.""
}}
";

    /// <summary>
    /// Gives a wired adapter of THIS PC a fixed address outside the usual DHCP range (.200-.250),
    /// keeping its gateway and DNS. Must run locally: run over WinRM, changing the address would
    /// cut the session it runs in. Reverts to DHCP if the gateway stops answering afterwards.
    /// </summary>
    public static string GetStaticIpScript(string adapterName) => ReportFn + $@"
$ErrorActionPreference = 'SilentlyContinue'
$a = Get-NetAdapter -Name '{Q(adapterName)}'
if (-not $a) {{ Report fail 'Network adapter not found' '{Q(adapterName)}'; return }}
$idx = $a.ifIndex
$cur = Get-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 | Where-Object {{ $_.IPAddress -notlike '169.254*' }} | Select-Object -First 1
$gw = (Get-NetRoute -InterfaceIndex $idx -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric | Select-Object -First 1).NextHop
$dns = @((Get-DnsClientServerAddress -InterfaceIndex $idx -AddressFamily IPv4).ServerAddresses)
if (-not $cur -or -not $gw) {{ Report fail 'No working connection on this adapter' 'Connect the network cable and check that the internet works, then try again.'; return }}
if ($cur.PrefixLength -ne 24) {{ Report fail 'Unusual network size' ""Subnet /$($cur.PrefixLength) is not supported automatically; reserve the address in the router instead.""; return }}
if ($dns.Count -eq 0) {{ $dns = @($gw) }}

# Pick a free address from .200-.250: no ping reply and no neighbour entry
$base = ($cur.IPAddress -split '\.')[0..2] -join '.'
$new = $null
foreach ($n in 200..250) {{
    $cand = ""$base.$n""
    if ($cand -eq $cur.IPAddress -or $cand -eq $gw) {{ continue }}
    if (Test-Connection $cand -Count 1 -Quiet) {{ continue }}
    if (Get-NetNeighbor -IPAddress $cand | Where-Object {{ $_.State -in 'Reachable', 'Stale', 'Permanent' }}) {{ continue }}
    $new = $cand; break
}}
if (-not $new) {{ Report fail 'No free address found in .200-.250' 'Reserve the address in the router instead.'; return }}

function Restore-Dhcp {{
    Remove-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -Confirm:$false
    Remove-NetRoute -InterfaceIndex $idx -DestinationPrefix '0.0.0.0/0' -Confirm:$false
    Set-NetIPInterface -InterfaceIndex $idx -Dhcp Enabled
    Set-DnsClientServerAddress -InterfaceIndex $idx -ResetServerAddresses
    ipconfig /renew ""$($a.Name)"" | Out-Null
}}

Remove-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -Confirm:$false
Remove-NetRoute -InterfaceIndex $idx -DestinationPrefix '0.0.0.0/0' -Confirm:$false
Set-NetIPInterface -InterfaceIndex $idx -Dhcp Disabled
New-NetIPAddress -InterfaceIndex $idx -IPAddress $new -PrefixLength 24 -DefaultGateway $gw | Out-Null
Set-DnsClientServerAddress -InterfaceIndex $idx -ServerAddresses $dns
Start-Sleep -Seconds 4

$okGw = Test-Connection $gw -Count 2 -Quiet
$okIp = (Get-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4).IPAddress -contains $new
if (-not ($okGw -and $okIp)) {{
    Restore-Dhcp
    Report fail 'Fixed IP did not work, so it was undone' ""The router stopped answering on $new. The adapter is back on automatic (DHCP); reserve the address in the router instead.""
    return
}}
Report ok ""Fixed IP set: $new"" ""Was $($cur.IPAddress) from DHCP. Gateway $gw and DNS $($dns -join ', ') were kept.""
if (-not (Resolve-DnsName www.microsoft.com -QuickTimeout)) {{ Report warn 'The internet does not answer yet' 'The local network works. If the internet stays down, run the wizard again or set the adapter back to automatic in Settings.' }}
";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static WizardHostInfo? ParseHostInfo(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonSerializer.Deserialize<WizardHostInfo>(output[start..(end + 1)], JsonOptions); }
        catch (JsonException) { return null; }
    }

    public static List<WizardStep> ParseSteps(string output) =>
        output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("RWD|"))
            .Select(l => l.Split('|', 4))
            .Where(p => p.Length == 4)
            .Select(p => new WizardStep(p[1], p[2], p[3]))
            .ToList();
}
