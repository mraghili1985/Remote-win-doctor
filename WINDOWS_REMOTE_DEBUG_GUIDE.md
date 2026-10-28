# راهنمای جامع و گام‌به‌گام دیباگ و بهینه‌سازی از راه دور ویندوز (از طریق وای‌فای مشترک)

این مستند شامل تمام دستورات، ترفندها و مراحل آزمایش‌شده برای اتصال، عیب‌یابی و بهینه‌سازی سیستم‌های ویندوزی در یک شبکه محلی (LAN / Wi-Fi) است.

---

## 📋 فهرست مراحل
1. [گام ۱: آماده‌سازی سیستم مقصد (سیستم مشتری)](#گام-۱-آماده‌سازی-سیستم-مقصد-سیستم-مشتری)
2. [گام ۲: آماده‌سازی سیستم مبدأ (سیستم شما)](#گام-۲-آماده‌سازی-سیستم-مبدأ-سیستم-شما)
3. [گام ۳: پیدا کردن IP سیستم‌ها در شبکه محلی](#گام-۳-پیدا-کردن-ip-سیستم‌ها-در-شبکه-محلی)
4. [گام ۴: تست پورت و اتصال ریموت](#گام-۴-تست-پورت-و-اتصال-ریموت)
5. [گام ۵: اسکریپت تشخیصی عمیق (استخراج مشخصات و گلوگاه‌ها)](#گام-۵-اسکریپت-تشخیصی-عمیق-استخراج-مشخصات-و-گلوگاه‌ها)
6. [گام ۶: اعمال بسته بهینه‌سازی ایمن (بدون دستکاری اطلاعات مشتری)](#گام-۶-اعمال-بسته-بهینه‌سازی-ایمن-بدون-دستکاری-اطلاعات-مشتری)
7. [گام ۷: اعتبارسنجی نهایی پس از ری‌استارت](#گام-۷-اعتبارسنجی-نهایی-پس-از-ری‌استارت)

---

## گام ۱: آماده‌سازی سیستم مقصد (سیستم مشتری)

به دلیل مسائل امنیتی در ویندوز ۱۱ و شبکه‌های وای‌فای، شبکه به صورت پیش‌فرض روی **Public** قرار دارد که مانع باز شدن فایروال می‌شود.

روی سیستم مقصد، **PowerShell را به صورت Run as Administrator** باز کنید و دستورات زیر را اجرا کنید:

```powershell
# ۱. تغییر شبکه وای‌فای از حالت عمومی به خصوصی (اجازه باز شدن پورت فایروال)
Set-NetConnectionProfile -NetworkCategory Private

# ۲. فعال‌سازی ریموت‌پاورشل (WinRM)
Enable-PSRemoting -SkipNetworkProfileCheck -Force

# ۳. دریافت آی‌پی محلی سیستم مقصد
(Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -notmatch 'Loopback|vEthernet' }).IPAddress
```

> **نکته:** حتماً نام‌کاربری (Username) و رمز عبور ویندوز آن سیستم را یادداشت کنید.

---

## گام ۲: آماده‌سازی سیستم مبدأ (سیستم شما)

برای اینکه ویندوز شما اجازه دهد به عنوان کلاینت به IP ناشناس در شبکه متصل شوید، باید سرویس WinRM روشن شده و TrustedHosts تنظیم شود.

روی سیستم خودتان، **PowerShell (Run as Administrator)** را باز کنید و دستورات زیر را اجرا کنید:

```powershell
# ۱. استارت سرویس مدیریت ریموت ویندوز
Start-Service WinRM

# ۲. اعتماد به تمامی IPهای شبکه محلی برای ارسال ایمن دستورات
Set-Item WSMan:\localhost\Client\TrustedHosts -Value * -Force
```

---

## گام ۳: پیدا کردن IP سیستم‌ها در شبکه محلی

اگر IP سیستم مقصد را نمی‌دانید، با دستور زیر در PowerShell کل رنج شبکه (`192.168.1.x`) اسکن شده و لیست دستگاه‌های روشن نمایش داده می‌شود:

```powershell
# اسکن سریع پینگ در ساب‌نت محلی
1..254 | ForEach-Object { (New-Object System.Net.NetworkInformation.Ping).SendPingAsync("192.168.1.$_", 200) } | Out-Null

# مشاهده آی‌پی‌های فعال در شبکه
Get-NetNeighbor -AddressFamily IPv4 | Where-Object { $_.IPAddress -like "192.168.1.*" -and $_.State -in "Reachable","Stale","Probe" -and $_.LinkLayerAddress -notmatch "00-00-00-00-00-00|FF-FF-FF-FF-FF-FF" } | Select-Object IPAddress, LinkLayerAddress, State | Sort-Object IPAddress
```

---

## گام ۴: تست پورت و اتصال ریموت

ابتدا باز بودن پورت ۵۹۸۵ (WinRM) را روی سیستم مقصد بررسی کنید:

```powershell
Test-NetConnection 192.168.1.100 -Port 5985
```
*(اگر `TcpTestSucceeded : True` بود، یعنی ارتباط شبکه برقرار است).*

سپس با نام کاربری و رمز عبور اتصال را تست کنید:

```powershell
$targetIP = "192.168.1.100"
$username = "Amene"
$password = "1289176868"

$sec = ConvertTo-SecureString $password -AsPlainText -Force
$cred = New-Object System.Management.Automation.PSCredential($username, $sec)

# تست فرمان ساده
Invoke-Command -ComputerName $targetIP -Credential $cred -ScriptBlock { hostname; (Get-CimInstance Win32_OperatingSystem).Caption }
```

---

## گام ۵: اسکریپت تشخیصی عمیق (استخراج مشخصات و گلوگاه‌ها)

با این دستور تمام سخت‌افزار، سلامت SSD، رم آزاد، برنامه‌های سنگین و خطاهای ثبت‌شده در Event Viewer خوانده می‌شود:

```powershell
Invoke-Command -ComputerName $targetIP -Credential $cred -ScriptBlock {
    Write-Host "=== 1. مدل پردازنده و کلاک ===" -ForegroundColor Cyan
    Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, CurrentClockSpeed

    Write-Host "=== 2. وضعیت رم ===" -ForegroundColor Cyan
    $os = Get-CimInstance Win32_OperatingSystem
    [PSCustomObject]@{
        TotalRAM_GB = [math]::Round($os.TotalVisibleMemorySize/1MB, 2)
        FreeRAM_GB  = [math]::Round($os.FreePhysicalMemory/1MB, 2)
        PercentFree = [math]::Round(($os.FreePhysicalMemory / $os.TotalVisibleMemorySize) * 100, 1)
    }

    Write-Host "=== 3. سلامت و نوع دیسک‌ها ===" -ForegroundColor Cyan
    Get-PhysicalDisk | Select-Object FriendlyName, MediaType, BusType, HealthStatus, OperationalStatus, @{N="Size(GB)";E={[math]::Round($_.Size/1GB, 1)}}

    Write-Host "=== 4. فضای درایوها ===" -ForegroundColor Cyan
    Get-Volume | Where-Object { $_.DriveLetter } | Select-Object DriveLetter, FileSystem, @{N="Size(GB)";E={[math]::Round($_.Size/1GB,1)}}, @{N="Free(GB)";E={[math]::Round($_.SizeRemaining/1GB,1)}}, @{N="%Free";E={[math]::Round(($_.SizeRemaining/$_.Size)*100,1)}}

    Write-Host "=== 5. مصرف لحظه‌ای CPU ===" -ForegroundColor Cyan
    Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 1 -MaxSamples 2 | ForEach-Object {
        $_.CounterSamples | Select-Object InstanceName, @{N="CPU_Load_%";E={[math]::Round($_.CookedValue, 1)}}
    }

    Write-Host "=== 6. پروسه‌های سنگین پس‌زمینه ===" -ForegroundColor Cyan
    Get-Process | Sort-Object CPU -Descending | Select-Object -First 10 Id, ProcessName, @{N="CPU_Sec";E={[math]::Round($_.CPU, 1)}}, @{N="RAM_MB";E={[math]::Round($_.WorkingSet64/1MB, 1)}}, Description | Format-Table -AutoSize

    Write-Host "=== 7. خطاهای مهم سیستم در ۲۴ ساعت گذشته ===" -ForegroundColor Cyan
    Get-WinEvent -FilterHashtable @{LogName='System'; Level=1,2; StartTime=(Get-Date).AddHours(-24)} -MaxEvents 10 -ErrorAction SilentlyContinue | 
        Select-Object TimeCreated, ProviderName, Id, Message | Format-Table -AutoSize -Wrap
}
```

---

## گام ۶: اعمال بسته بهینه‌سازی ایمن (بدون دستکاری اطلاعات مشتری)

این اسکریپت هیچ فایلی از دسکتاپ، اسناد، دانلودها یا درایوهای مشتری را حذف نمی‌کند و صرفاً سرویس‌های سربار و فایل‌های موقت سیستمی را بهینه‌سازی می‌کند:

```powershell
Invoke-Command -ComputerName $targetIP -Credential $cred -ScriptBlock {
    Write-Host ">>> 1. خاموش کردن سرویس‌های سنگین و کرش‌کننده تله‌متری HP..." -ForegroundColor Yellow
    $hpServices = @("HPSysInfoCap", "HpTouchpointAnalyticsService", "HPAppHelperCap", "HPDiagsCap", "HPNetworkCap")
    foreach ($svc in $hpServices) {
        if (Get-Service -Name $svc -ErrorAction SilentlyContinue) {
            Stop-Service -Name $svc -Force -ErrorAction SilentlyContinue
            Set-Service -Name $svc -StartupType Disabled -ErrorAction SilentlyContinue
            Write-Host "  [غیرفعال شد] $svc" -ForegroundColor Green
        }
    }

    Write-Host ">>> 2. غیرفعال‌سازی SysMain برای جلوگیری از درگیری دیسک..." -ForegroundColor Yellow
    if (Get-Service -Name "SysMain" -ErrorAction SilentlyContinue) {
        Stop-Service -Name "SysMain" -Force -ErrorAction SilentlyContinue
        Set-Service -Name "SysMain" -StartupType Disabled -ErrorAction SilentlyContinue
        Write-Host "  [غیرفعال شد] SysMain" -ForegroundColor Green
    }

    Write-Host ">>> 3. ریست کش معیوب آپدیت‌های ویندوز..." -ForegroundColor Yellow
    Stop-Service -Name wuauserv -Force -ErrorAction SilentlyContinue
    Stop-Service -Name bits -Force -ErrorAction SilentlyContinue
    Remove-Item -Path "C:\Windows\SoftwareDistribution\Download\*" -Recurse -Force -ErrorAction SilentlyContinue
    Start-Service -Name bits -ErrorAction SilentlyContinue
    Start-Service -Name wuauserv -ErrorAction SilentlyContinue
    Write-Host "  [ریست شد] Windows Update Cache" -ForegroundColor Green

    Write-Host ">>> 4. پاکسازی فایل‌های تمپ ویندوز..." -ForegroundColor Yellow
    Remove-Item -Path "C:\Windows\Temp\*" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path "$env:LOCALAPPDATA\Temp\*" -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  [پاکسازی شد] فولدارهای Temp" -ForegroundColor Green

    Write-Host ">>> 5. تنظیم پلن مصرف برق روی حداکثر راندمان (High Performance)..." -ForegroundColor Yellow
    $highPerf = (powercfg /l | Select-String "High performance|8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")
    if (-not $highPerf) {
        powercfg -duplicatescheme 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c
    }
    powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c
    Write-Host "  [فعال شد] High Performance Power Plan" -ForegroundColor Green

    Write-Host ">>> 6. غیرفعال‌سازی Fast Startup (جلوگیری از انباشت باگ در رم)..." -ForegroundColor Yellow
    Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power" -Name "HiberbootEnabled" -Value 0
    Write-Host "  [غیرفعال شد] Fast Startup" -ForegroundColor Green

    Write-Host ">>> 7. اجرای دستور TRIM روی SSD برای افزایش سرعت خواندن/نوشتن..." -ForegroundColor Yellow
    Optimize-Volume -DriveLetter C -ReTrim -Verbose
    Write-Host "  [انجام شد] SSD ReTrim" -ForegroundColor Green
}
```

---

## گام ۷: اعتبارسنجی نهایی پس از ری‌استارت

پس از ری‌استارت سیستم مقصد، دستور زیر را اجرا کنید تا وضعیت پردازنده و رم را پس از بوت تمیز مشاهده نمایید:

```powershell
Invoke-Command -ComputerName $targetIP -Credential $cred -ScriptBlock {
    $os = Get-CimInstance Win32_OperatingSystem
    Write-Host "=== وضعیت پس از بوت تمیز ===" -ForegroundColor Cyan
    [PSCustomObject]@{
        'زمان از آخرین بوت (دقیقه)' = [math]::Round(((Get-Date) - $os.LastBootUpTime).TotalMinutes, 1)
        'رم آزاد (GB)'              = [math]::Round($os.FreePhysicalMemory/1MB, 2)
        'درصد رم آزاد'              = [math]::Round(($os.FreePhysicalMemory / $os.TotalVisibleMemorySize) * 100, 1)
    }

    Write-Host "=== مصرف پردازنده ===" -ForegroundColor Cyan
    Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 1 -MaxSamples 2 | ForEach-Object {
        $_.CounterSamples | Select-Object InstanceName, @{N="CPU_Load_%";E={[math]::Round($_.CookedValue, 1)}}
    }
}
```

*(در صورت اجرای صحیح، لود پردازنده باید زیر ۵٪ و رم آزاد به بالای ۷۰٪ برسد).*
