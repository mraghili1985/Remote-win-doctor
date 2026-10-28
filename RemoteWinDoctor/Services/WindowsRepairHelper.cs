namespace RemoteWinDoctor.Services;

/// <summary>One row of the Windows Repairs scan. Status is ok, problem or info.</summary>
public record RepairCheck(string Id, string Status, bool CanFix, string Title, string Detail);

/// <summary>
/// Targeted registry repairs for symptoms malware and bad tweaks leave behind (hijacked .exe/.lnk
/// associations, locked Task Manager/Regedit, IFEO debuggers, stale proxy, CD/DVD filters).
/// Deliberately NOT a generic registry cleaner: every check compares against a known Windows
/// default, every fix exports the keys it touches first and re-reads them afterwards.
/// Over WinRM HKCU is the remote login account, so per-user checks walk every loaded hive in HKU.
/// </summary>
public static class WindowsRepairHelper
{
    private const string Prelude = @"
$ErrorActionPreference = 'SilentlyContinue'
function Check($id, $status, $fix, $title, $detail) { Write-Output ('RWC|{0}|{1}|{2}|{3}|{4}' -f $id, $status, $fix, $title, $detail) }
function Report($status, $title, $detail) { Write-Output ""RWD|$status|$title|$detail"" }
function RegVal($path, $name) { $k = Get-Item -LiteralPath ('Registry::' + $path); if ($k) { $k.GetValue($name, $null, 'DoNotExpandEnvironmentNames') } }
function HasKey($path) { Test-Path -LiteralPath ('Registry::' + $path) }
function UserName($sid) { try { (New-Object Security.Principal.SecurityIdentifier $sid).Translate([Security.Principal.NTAccount]).Value } catch { $sid } }
$sids = @(Get-ChildItem 'Registry::HKEY_USERS' | Where-Object { $_.PSChildName -match '^S-1-5-21-[\d-]+$' } | ForEach-Object { $_.PSChildName })

$assoc = [ordered]@{ '.exe' = 'exefile'; '.lnk' = 'lnkfile'; '.bat' = 'batfile'; '.cmd' = 'cmdfile'; '.com' = 'comfile'; '.msi' = 'Msi.Package' }
$cmdKeys = 'exefile', 'batfile', 'cmdfile', 'comfile'
$openCmd = '""%1"" %*'

function Get-AssocProblems {
    foreach ($ext in $assoc.Keys) {
        $v = RegVal ""HKEY_LOCAL_MACHINE\SOFTWARE\Classes\$ext"" ''
        if ($v -ne $assoc[$ext]) { [pscustomobject]@{ Kind = 'hklm'; Path = $null; Text = ""$ext is linked to '$v' instead of $($assoc[$ext])""; Target = $null } }
    }
    foreach ($c in $cmdKeys) {
        $v = RegVal ""HKEY_LOCAL_MACHINE\SOFTWARE\Classes\$c\shell\open\command"" ''
        if ($v -ne $openCmd) { [pscustomobject]@{ Kind = 'hklm'; Path = $null; Text = ""$c opens through: $v""; Target = $v } }
    }
    if ($null -eq (RegVal 'HKEY_LOCAL_MACHINE\SOFTWARE\Classes\lnkfile' 'IsShortcut')) {
        [pscustomobject]@{ Kind = 'hklm'; Path = $null; Text = 'lnkfile lost its IsShortcut flag'; Target = $null }
    }
    foreach ($sid in $sids) {
        $u = UserName $sid
        foreach ($ext in $assoc.Keys) {
            $p = ""HKEY_USERS\$sid\Software\Classes\$ext""
            if (-not (HasKey $p)) { continue }
            $d = RegVal $p ''
            $cmd = RegVal ""$p\shell\open\command"" ''
            if (-not $cmd -and $d) { $cmd = RegVal ""HKEY_USERS\$sid\Software\Classes\$d\shell\open\command"" '' }
            if (($d -and $d -ne $assoc[$ext]) -or $cmd) {
                [pscustomobject]@{ Kind = 'user'; Path = $p; Text = ""$u overrides $ext -> $(if ($cmd) { $cmd } else { $d })""; Target = $cmd }
            }
        }
        foreach ($cls in 'exefile', 'lnkfile') {
            $p = ""HKEY_USERS\$sid\Software\Classes\$cls""
            if ((HasKey ""$p\shell"") -or (HasKey ""$p\DefaultIcon"")) {
                [pscustomobject]@{ Kind = 'user'; Path = $p; Text = ""$u overrides $cls""; Target = (RegVal ""$p\shell\open\command"" '') }
            }
        }
        foreach ($ext in '.lnk', '.exe') {
            $p = ""HKEY_USERS\$sid\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\$ext\UserChoice""
            if (HasKey $p) {
                $prog = RegVal $p 'ProgId'
                [pscustomobject]@{ Kind = 'choice'; Path = $p; Text = ""$u set 'Open with' for $ext to $prog""; Target = $(if ($prog -like 'Applications\*') { $prog.Substring(13) }) }
            }
        }
    }
}

function Get-TargetPath($cmd) {
    if ($cmd -match '^\s*""?([^""]+?\.(exe|com|bat|cmd|scr|vbs|js))') { $matches[1] } else { $null }
}

$policyChecks = @(
    @{ Key = 'Software\Microsoft\Windows\CurrentVersion\Policies\System'; Name = 'DisableTaskMgr'; Label = 'Task Manager is blocked' },
    @{ Key = 'Software\Microsoft\Windows\CurrentVersion\Policies\System'; Name = 'DisableRegistryTools'; Label = 'Registry Editor is blocked' },
    @{ Key = 'Software\Policies\Microsoft\Windows\System'; Name = 'DisableCMD'; Label = 'Command Prompt is blocked' },
    @{ Key = 'Software\Microsoft\Windows\CurrentVersion\Policies\Explorer'; Name = 'NoRun'; Label = 'Run dialog is hidden' },
    @{ Key = 'Software\Microsoft\Windows\CurrentVersion\Policies\Explorer'; Name = 'NoFolderOptions'; Label = 'Folder Options is hidden' },
    @{ Key = 'Software\Microsoft\Windows\CurrentVersion\Policies\Explorer'; Name = 'NoControlPanel'; Label = 'Control Panel and Settings are blocked' }
)
$showAll = 'HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced\Folder\Hidden\SHOWALL'

function Get-PolicyProblems {
    $roots = @(@{ Path = 'HKEY_LOCAL_MACHINE'; Who = 'all users' }) + @($sids | ForEach-Object { @{ Path = ""HKEY_USERS\$_""; Who = (UserName $_) } })
    foreach ($r in $roots) {
        foreach ($pc in $policyChecks) {
            $p = ""$($r.Path)\$($pc.Key)""
            $v = RegVal $p $pc.Name
            if ($v -and [int]$v -ne 0) { [pscustomobject]@{ Path = $p; Name = $pc.Name; Text = ""$($pc.Label) ($($r.Who))"" } }
        }
    }
    $cv = RegVal $showAll 'CheckedValue'
    if ($null -ne $cv -and [int]$cv -ne 1) { [pscustomobject]@{ Path = $showAll; Name = 'CheckedValue'; Text = 'Show hidden files option is disabled (common USB-virus trick)' } }
}

$ifeoRoots = 'HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options',
             'HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Image File Execution Options'

function Get-IfeoProblems {
    # IFEO is shared between the 64- and 32-bit views, so the same entry shows up under both roots
    $seen = @{}
    foreach ($root in $ifeoRoots) {
        foreach ($k in Get-ChildItem -LiteralPath ('Registry::' + $root)) {
            $dbg = $k.GetValue('Debugger')
            if ($dbg -and -not $seen.ContainsKey($k.PSChildName + '|' + $dbg)) {
                $seen[$k.PSChildName + '|' + $dbg] = 1
                [pscustomobject]@{ Path = ""$root\$($k.PSChildName)""; Exe = $k.PSChildName; Debugger = $dbg }
            }
        }
    }
}

$cdClass = 'HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Class\{4d36e965-e325-11ce-bfc1-08002be10318}'
";

    private const string FixPrelude = @"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Report 'fail' 'Administrator rights needed' 'Run RemoteWinDoctor as administrator and try again.'; return }
$bk = Join-Path $env:ProgramData ('RemoteWinDoctor\RegBackup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $bk -Force | Out-Null
$script:bkn = 0
function Backup($path) {
    if (HasKey $path) { $script:bkn++; reg.exe export $path (Join-Path $bk ('{0:00}.reg' -f $script:bkn)) /y 2>$null | Out-Null }
}
function Done {
    if ($script:bkn -gt 0) { Report 'info' 'Backup saved' ""$bk - double-click a .reg file there to undo."" }
    else { Remove-Item -LiteralPath $bk -Force }
}
";

    public static string GetScanScript() => Prelude + @"
# 1. File associations
$probs = @(Get-AssocProblems)
if ($probs.Count -eq 0) { Check 'assoc' 'ok' 0 'Program and shortcut associations' '.exe, .lnk, .bat, .cmd, .com and .msi open the Windows way.' }
else {
    $paths = @($probs | ForEach-Object { Get-TargetPath $_.Target } | Where-Object { $_ } | Select-Object -Unique)
    $extra = if ($paths.Count) { ' | Points to: ' + (($paths | ForEach-Object { if (Test-Path -LiteralPath $_) { ""$_ (still on disk)"" } else { $_ } }) -join ', ') } else { '' }
    Check 'assoc' 'problem' 1 'Program and shortcut associations' ((($probs | ForEach-Object { $_.Text }) -join '; ') + $extra)
}

# 2. Icon cache
$size = 0; $n = 0
foreach ($u in Get-ChildItem -LiteralPath (Join-Path $env:SystemDrive 'Users') -Directory) {
    $l = Join-Path $u.FullName 'AppData\Local'
    foreach ($f in @(Get-Item -LiteralPath (Join-Path $l 'IconCache.db')) + @(Get-ChildItem -Path (Join-Path $l 'Microsoft\Windows\Explorer') -Filter 'iconcache_*.db')) {
        if ($f) { $size += $f.Length; $n++ }
    }
}
Check 'iconcache' 'info' 1 'Icon cache' ('{0} cache files, {1:N1} MB. Rebuild it when icons look wrong or blank, or after fixing associations.' -f $n, ($size / 1MB))

# 3. Blocked system tools
$pol = @(Get-PolicyProblems)
$domain = (Get-CimInstance Win32_ComputerSystem).PartOfDomain
if ($pol.Count -eq 0) { Check 'policies' 'ok' 0 'Blocked system tools' 'Task Manager, Registry Editor, Command Prompt, Run and Folder Options are not blocked.' }
else {
    $note = if ($domain) { ' | This PC is on a domain: these may be company policy and can come back.' } else { '' }
    Check 'policies' 'problem' 1 'Blocked system tools' ((($pol | ForEach-Object { $_.Text }) -join '; ') + $note)
}

# 4. Hijacked programs (IFEO debugger)
$ifeo = @(Get-IfeoProblems)
if ($ifeo.Count -eq 0) { Check 'ifeo' 'ok' 0 'Hijacked programs' 'No program is redirected to another one when it starts.' }
else { Check 'ifeo' 'problem' 1 'Hijacked programs' (($ifeo | ForEach-Object { ""$($_.Exe) starts $($_.Debugger) instead"" }) -join '; ') }

# 5. Proxy
$px = @()
foreach ($sid in $sids) {
    $p = ""HKEY_USERS\$sid\Software\Microsoft\Windows\CurrentVersion\Internet Settings""
    if ((RegVal $p 'ProxyEnable') -eq 1) { $px += ""$(UserName $sid): proxy $(RegVal $p 'ProxyServer')"" }
    $pac = RegVal $p 'AutoConfigURL'
    if ($pac) { $px += ""$(UserName $sid): setup script $pac"" }
}
if ($px.Count -eq 0) { Check 'proxy' 'ok' 0 'Proxy settings' 'No proxy is set; connections go out directly.' }
else { Check 'proxy' 'info' 1 'Proxy settings' (($px -join '; ') + ' | Fine if a VPN or proxy app you use set it. If websites do not open with that app closed, turn it off.') }

# 6. CD/DVD drive filters
$up = RegVal $cdClass 'UpperFilters'; $low = RegVal $cdClass 'LowerFilters'
$filters = @(@($up) + @($low) | Where-Object { $_ })
$bad = @(Get-CimInstance Win32_PnPEntity -Filter ""PNPClass='CDROM'"" | Where-Object { $_.ConfigManagerErrorCode -ne 0 })
$any = @(Get-CimInstance Win32_PnPEntity -Filter ""PNPClass='CDROM'"").Count
if ($bad.Count -gt 0 -and $filters.Count -gt 0) {
    Check 'cdfilters' 'problem' 1 'CD/DVD drive' (""$($bad[0].Name) has error code $($bad[0].ConfigManagerErrorCode); leftover filter drivers: $($filters -join ', ')"")
} elseif ($bad.Count -gt 0) {
    Check 'cdfilters' 'problem' 0 'CD/DVD drive' (""$($bad[0].Name) has error code $($bad[0].ConfigManagerErrorCode). No filter drivers, so this is a driver or hardware problem."")
} elseif ($any -eq 0) {
    Check 'cdfilters' 'ok' 0 'CD/DVD drive' $(if ($filters.Count) { ""No drive present; filters $($filters -join ', ') left alone."" } else { 'No CD/DVD drive present.' })
} else {
    Check 'cdfilters' 'ok' 0 'CD/DVD drive' $(if ($filters.Count) { ""Working. Filters from burning software ($($filters -join ', ')) left alone."" } else { 'Working.' })
}

# 7. Windows Update services
$wu = @('wuauserv', 'BITS', 'CryptSvc', 'UsoSvc') | ForEach-Object { Get-CimInstance Win32_Service -Filter ""Name='$_'"" } | Where-Object { $_.StartMode -eq 'Disabled' }
if (@($wu).Count -eq 0) { Check 'wu' 'ok' 0 'Windows Update services' 'Update, BITS, Cryptographic and Orchestrator services can start.' }
else { Check 'wu' 'info' 1 'Windows Update services' ('Disabled: ' + (($wu | ForEach-Object { $_.DisplayName }) -join ', ') + ' | Fine if updates were blocked on purpose; Store and Defender updates also need them.') }

# 8. Malware scan
$mp = Get-MpComputerStatus
if (-not $mp) { Check 'defender' 'info' 0 'Malware scan (Defender)' 'Defender status is unavailable; another antivirus is probably in charge.' }
else {
    $parts = @()
    if (-not $mp.RealTimeProtectionEnabled) { $parts += 'Real-time protection is off' }
    $parts += ""Definitions $($mp.AntivirusSignatureAge) day(s) old""
    if ($mp.QuickScanEndTime) { $parts += ""last quick scan $([int]((Get-Date) - $mp.QuickScanEndTime).TotalDays) day(s) ago"" } else { $parts += 'no quick scan on record' }
    $threats = @(Get-MpThreat)
    if ($threats.Count) { $parts += ""$($threats.Count) threat(s) in history"" }
    Check 'defender' 'info' $(if ($mp.AMServiceEnabled) { 1 } else { 0 }) 'Malware scan (Defender)' ($parts -join ', ')
}
";

    /// <param name="local">True when the target is this PC; only then can Explorer be started again in the user's session.</param>
    public static string GetFixScript(string id, bool local) => Prelude + FixPrelude + $"$local = ${local.ToString().ToLowerInvariant()}\n" + id switch
    {
        "assoc" => @"
$probs = @(Get-AssocProblems)
$paths = @($probs | ForEach-Object { Get-TargetPath $_.Target } | Where-Object { $_ } | Select-Object -Unique)
if (@($probs | Where-Object Kind -eq 'hklm').Count) {
    foreach ($ext in $assoc.Keys) { Backup ""HKEY_LOCAL_MACHINE\SOFTWARE\Classes\$ext"" }
    foreach ($c in $cmdKeys + 'lnkfile') { Backup ""HKEY_LOCAL_MACHINE\SOFTWARE\Classes\$c"" }
    foreach ($ext in $assoc.Keys) { [Microsoft.Win32.Registry]::SetValue(""HKEY_LOCAL_MACHINE\SOFTWARE\Classes\$ext"", '', $assoc[$ext]) }
    foreach ($c in $cmdKeys) {
        $k = ""HKEY_LOCAL_MACHINE\SOFTWARE\Classes\$c\shell\open\command""
        [Microsoft.Win32.Registry]::SetValue($k, '', $openCmd)
        if ($c -eq 'exefile' -or $null -ne (RegVal $k 'IsolatedCommand')) { [Microsoft.Win32.Registry]::SetValue($k, 'IsolatedCommand', $openCmd) }
    }
    if ($null -eq (RegVal 'HKEY_LOCAL_MACHINE\SOFTWARE\Classes\lnkfile' 'IsShortcut')) { [Microsoft.Win32.Registry]::SetValue('HKEY_LOCAL_MACHINE\SOFTWARE\Classes\lnkfile', 'IsShortcut', '') }
}
foreach ($pr in $probs | Where-Object Kind -ne 'hklm') {
    Backup $pr.Path
    Remove-Item -LiteralPath ('Registry::' + $pr.Path) -Recurse -Force
    if ($pr.Kind -eq 'choice') {
        $parent = Split-Path $pr.Path -Parent
        foreach ($sub in 'OpenWithList', 'OpenWithProgids') { Backup ""$parent\$sub""; Remove-Item -LiteralPath ('Registry::' + ""$parent\$sub"") -Recurse -Force }
    }
}
$left = @(Get-AssocProblems)
if ($probs.Count -eq 0) { Report 'ok' 'Associations' 'Nothing to fix.' }
elseif ($left.Count -eq 0) { Report 'ok' 'Associations restored' ""$($probs.Count) setting(s) put back to the Windows default."" }
else { Report 'fail' 'Some associations could not be restored' (($left | ForEach-Object { $_.Text }) -join '; ') }
foreach ($t in $paths) {
    if (Test-Path -LiteralPath $t) { Report 'warn' 'Program that took over is still on disk' ""$t - scan it with Defender; if it is malware it will change these settings again."" }
}
if ($probs.Count) { Report 'info' 'Refresh icons' 'Run Rebuild icon cache, or sign out and back in, to see the right icons.' }
Done
",
        "iconcache" => @"
Stop-Process -Name explorer -Force
Start-Sleep -Seconds 2
$n = 0
foreach ($u in Get-ChildItem -LiteralPath (Join-Path $env:SystemDrive 'Users') -Directory) {
    $l = Join-Path $u.FullName 'AppData\Local'
    foreach ($f in @(Get-Item -LiteralPath (Join-Path $l 'IconCache.db')) + @(Get-ChildItem -Path (Join-Path $l 'Microsoft\Windows\Explorer') -Filter 'iconcache_*.db')) {
        if ($f) { Remove-Item -LiteralPath $f.FullName -Force; if (-not (Test-Path -LiteralPath $f.FullName)) { $n++ } }
    }
}
Report 'ok' 'Icon cache cleared' ""$n cache file(s) removed; Windows rebuilds them automatically.""
Start-Sleep -Seconds 3
if (-not (Get-Process explorer)) {
    if ($local) { Start-Process explorer.exe; Start-Sleep -Seconds 2 }
}
if (Get-Process explorer) { Report 'ok' 'Explorer restarted' 'Taskbar and desktop are back.' }
else { Report 'warn' 'Explorer did not restart' 'On the target PC press Ctrl+Shift+Esc, File > Run new task, type explorer and press Enter (or sign out and back in).' }
Done
",
        "policies" => @"
$pol = @(Get-PolicyProblems)
foreach ($p in $pol) {
    Backup $p.Path
    if ($p.Name -eq 'CheckedValue') { Set-ItemProperty -LiteralPath ('Registry::' + $p.Path) -Name 'CheckedValue' -Value 1 -Type DWord }
    else { Remove-ItemProperty -LiteralPath ('Registry::' + $p.Path) -Name $p.Name -Force }
}
$left = @(Get-PolicyProblems)
if ($pol.Count -eq 0) { Report 'ok' 'Blocked tools' 'Nothing to fix.' }
elseif ($left.Count -eq 0) { Report 'ok' 'System tools unblocked' (($pol | ForEach-Object { $_.Text }) -join '; ') }
else { Report 'fail' 'Some blocks could not be removed' (($left | ForEach-Object { $_.Text }) -join '; ') }
if ($pol.Count) { Report 'info' 'Sign out' 'Some blocks clear only after the user signs out and back in.' }
Done
",
        "ifeo" => @"
$ifeo = @(Get-IfeoProblems)
foreach ($i in $ifeo) { Backup $i.Path; Remove-ItemProperty -LiteralPath ('Registry::' + $i.Path) -Name 'Debugger' -Force }
$left = @(Get-IfeoProblems)
if ($ifeo.Count -eq 0) { Report 'ok' 'Hijacked programs' 'Nothing to fix.' }
elseif ($left.Count -eq 0) { Report 'ok' 'Redirects removed' (($ifeo | ForEach-Object { ""$($_.Exe) starts normally again"" }) -join '; ') }
else { Report 'fail' 'Some redirects could not be removed' (($left | ForEach-Object { $_.Exe }) -join ', ') }
foreach ($i in $ifeo) {
    $t = Get-TargetPath $i.Debugger
    if ($t -and (Test-Path -LiteralPath $t)) { Report 'warn' 'Redirect target still on disk' ""$t - scan it with Defender if you did not install it yourself."" }
}
Done
",
        "proxy" => @"
$n = 0
foreach ($sid in $sids) {
    $p = ""HKEY_USERS\$sid\Software\Microsoft\Windows\CurrentVersion\Internet Settings""
    if ((RegVal $p 'ProxyEnable') -eq 1 -or (RegVal $p 'AutoConfigURL')) {
        Backup $p
        Set-ItemProperty -LiteralPath ('Registry::' + $p) -Name 'ProxyEnable' -Value 0 -Type DWord
        Remove-ItemProperty -LiteralPath ('Registry::' + $p) -Name 'AutoConfigURL' -Force
        if ((RegVal $p 'ProxyEnable') -eq 0 -and -not (RegVal $p 'AutoConfigURL')) { $n++; Report 'ok' 'Proxy turned off' (UserName $sid) }
        else { Report 'fail' 'Proxy still set' (UserName $sid) }
    }
}
if ($n -eq 0) { Report 'ok' 'Proxy' 'Nothing to change.' }
else { Report 'info' 'If it comes back' 'A running VPN or proxy app sets it again; close or reconfigure that app.' }
Done
",
        "cdfilters" => @"
Backup $cdClass
foreach ($v in 'UpperFilters', 'LowerFilters') { Remove-ItemProperty -LiteralPath ('Registry::' + $cdClass) -Name $v -Force }
if ($null -eq (RegVal $cdClass 'UpperFilters') -and $null -eq (RegVal $cdClass 'LowerFilters')) {
    Report 'ok' 'CD/DVD filters removed' 'Restart the PC; the drive should appear in File Explorer. Burning software may need to be reinstalled.'
} else { Report 'fail' 'Filters could not be removed' $cdClass }
Done
",
        "wu" => @"
$want = [ordered]@{ wuauserv = 'Manual'; BITS = 'Manual'; CryptSvc = 'Automatic'; UsoSvc = 'Automatic' }
foreach ($name in $want.Keys) {
    $s = Get-CimInstance Win32_Service -Filter ""Name='$name'""
    if (-not $s -or $s.StartMode -ne 'Disabled') { continue }
    Backup ""HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\$name""
    Set-Service -Name $name -StartupType $want[$name]
    $s = Get-CimInstance Win32_Service -Filter ""Name='$name'""
    if ($s.StartMode -ne 'Disabled') { Report 'ok' $s.DisplayName ""Startup set to $($want[$name])"" }
    else { Report 'fail' $s.DisplayName 'Still disabled; an update-blocker tool may be protecting it.' }
}
Done
",
        "defender" => @"
$start = Get-Date
Start-MpScan -ScanType QuickScan
$new = @(Get-MpThreatDetection | Where-Object { $_.InitialDetectionTime -ge $start })
if ($new.Count -eq 0) { Report 'ok' 'Quick scan finished' 'No threats found.' }
else {
    $names = @($new | ForEach-Object { (Get-MpThreat -ThreatID $_.ThreatID).ThreatName } | Select-Object -Unique)
    Report 'warn' ""$($new.Count) threat(s) found"" (($names -join ', ') + ' - Defender handled them with its usual action; check Windows Security > Protection history.')
}
Done
",
        _ => "Report 'fail' 'Unknown repair' ''"
    };

    public static List<RepairCheck> ParseScan(string output) =>
        output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("RWC|"))
            .Select(l => l.Split('|', 6))
            .Where(p => p.Length == 6)
            .Select(p => new RepairCheck(p[1], p[2], p[3] == "1", p[4], p[5]))
            .ToList();

    public static string FixLabel(string id) => id switch
    {
        "assoc" => "Restore defaults",
        "iconcache" => "Rebuild icon cache",
        "policies" => "Unblock tools",
        "ifeo" => "Remove redirects",
        "proxy" => "Turn off proxy",
        "cdfilters" => "Remove filters",
        "wu" => "Re-enable services",
        "defender" => "Run quick scan",
        _ => "Fix"
    };

    public static string ConfirmText(string id) => id switch
    {
        "assoc" => "Put .exe, .lnk, .bat, .cmd, .com and .msi back to the Windows defaults and remove the 'Open with' choices listed?\n\nThe registry keys are backed up first.",
        "iconcache" => "Explorer will close and reopen on the target PC (taskbar and open folder windows disappear for a few seconds). Continue?",
        "policies" => "Remove the blocks listed (Task Manager, Registry Editor, etc.)?\n\nOn a company domain PC these may be intentional; ask the administrator first.",
        "ifeo" => "Remove the redirects listed? If one of them is a tool you installed on purpose (for example Process Explorer replacing Task Manager), that tool will no longer replace the original.",
        "proxy" => "Turn off the proxy for the users listed? If a VPN or proxy app you rely on set it, that app may stop working until it is reconnected.",
        "cdfilters" => "Remove the CD/DVD filter drivers? Burning software that installed them (Nero, Roxio, etc.) may need to be reinstalled. A restart is needed.",
        "wu" => "Set the disabled Windows Update services back to their default startup type?\n\nWindows will be able to download updates again.",
        "defender" => "Run a Defender quick scan? It takes a few minutes. Defender applies its usual action to anything it finds, which can include quarantining cracked software that is not in its exclusions.",
        _ => "Run this repair?"
    };
}
