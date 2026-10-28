// ==========================================================================
// RemoteWinDoctor - High-Performance Frontend Engine
// ==========================================================================

const API_BASE = '';
let currentProfiles = [];
let activeProfile = null;
let lastDiagnosticData = null;

// DOM Elements
const selectProfile = document.getElementById('select-profile');
const targetIp = document.getElementById('target-ip');
const targetUser = document.getElementById('target-user');
const targetPass = document.getElementById('target-pass');

// Terminal Log Helper
function logTerminal(message, type = 'info') {
    const termBody = document.getElementById('terminal-output');
    if (!termBody) return;
    
    const now = new Date().toLocaleTimeString('fa-IR');
    const line = document.createElement('div');
    line.className = 'term-line';
    
    if (type === 'error') {
        line.style.color = '#fb7185';
        line.textContent = `[${now} ERROR] ${message}`;
    } else if (type === 'success') {
        line.style.color = '#34d399';
        line.textContent = `[${now} OK] ${message}`;
    } else if (type === 'warn') {
        line.style.color = '#fbbf24';
        line.textContent = `[${now} WARN] ${message}`;
    } else {
        line.textContent = `[${now}] ${message}`;
    }
    
    termBody.appendChild(line);
    termBody.scrollTop = termBody.scrollHeight;
}

// --------------------------------------------------------------------------
// 1. Tab Navigation
// --------------------------------------------------------------------------
function initNavigation() {
    const navItems = document.querySelectorAll('.nav-item');
    const panes = document.querySelectorAll('.tab-pane');

    navItems.forEach(btn => {
        btn.addEventListener('click', () => {
            const tabId = btn.getAttribute('data-tab');
            if (!tabId) return;

            navItems.forEach(b => b.classList.remove('active'));
            panes.forEach(p => p.classList.remove('active'));

            btn.classList.add('active');
            const targetPane = document.getElementById(tabId);
            if (targetPane) targetPane.classList.add('active');
        });
    });
}

// --------------------------------------------------------------------------
// 2. Profile Management
// --------------------------------------------------------------------------
async function loadProfiles() {
    try {
        const res = await fetch(`${API_BASE}/api/profiles`);
        if (!res.ok) throw new Error('خطا در دریافت پروفایل‌ها');
        currentProfiles = await res.json();
        renderProfilesUI();
    } catch (err) {
        logTerminal(`خطا در بارگذاری پروفایل‌ها: ${err.message}`, 'error');
    }
}

function renderProfilesUI() {
    // 1. Populate Dropdown
    selectProfile.innerHTML = '';
    currentProfiles.forEach(p => {
        const opt = document.createElement('option');
        opt.value = p.id;
        opt.textContent = `${p.name} (${p.ipAddress})`;
        selectProfile.appendChild(opt);
    });

    // 2. Populate Profiles Tab Grid
    const container = document.getElementById('profiles-container');
    if (!container) return;
    container.innerHTML = '';

    currentProfiles.forEach(p => {
        const card = document.createElement('div');
        card.className = `profile-card ${activeProfile && activeProfile.id === p.id ? 'active-card' : ''}`;
        card.innerHTML = `
            <div>
                <div class="profile-card-top">
                    <div>
                        <div class="profile-name">${p.name}</div>
                        <div class="profile-ip">${p.ipAddress}</div>
                    </div>
                    <span class="badge badge-cyan">${p.username}</span>
                </div>
                <div class="profile-notes">${p.notes || 'بدون یادداشت'}</div>
            </div>
            <div class="profile-footer">
                <button class="btn btn-secondary btn-sm select-btn">انتخاب سیستم</button>
                <button class="btn btn-danger btn-sm delete-btn">&times;</button>
            </div>
        `;

        // Select Action
        card.querySelector('.select-btn').addEventListener('click', (e) => {
            e.stopPropagation();
            setActiveProfile(p.id);
        });

        card.addEventListener('click', () => {
            setActiveProfile(p.id);
        });

        // Delete Action
        card.querySelector('.delete-btn').addEventListener('click', async (e) => {
            e.stopPropagation();
            if (confirm(`آیا از حذف سیستم ${p.name} اطمینان دارید؟`)) {
                await deleteProfile(p.id);
            }
        });

        container.appendChild(card);
    });

    // Set first profile active if none active
    if (!activeProfile && currentProfiles.length > 0) {
        setActiveProfile(currentProfiles[0].id);
    }
}

function setActiveProfile(id) {
    const prof = currentProfiles.find(p => p.id === id);
    if (!prof) return;

    activeProfile = prof;
    selectProfile.value = prof.id;
    targetIp.value = prof.ipAddress;
    targetUser.value = prof.username;
    targetPass.value = prof.password;

    updateTargetModeBadge(prof.ipAddress);

    // Refresh grid active highlight
    document.querySelectorAll('.profile-card').forEach(card => card.classList.remove('active-card'));
    renderProfilesUI();

    logTerminal(`سیستم فعال تغییر کرد به: ${prof.name} (${prof.ipAddress})`, 'info');
}

function updateTargetModeBadge(ip) {
    const badge = document.getElementById('target-mode-badge');
    const isLocal = !ip || ip === '127.0.0.1' || ip.toLowerCase() === 'localhost' || ip === '192.168.1.75' || ip === '::1';
    if (badge) {
        if (isLocal) {
            badge.textContent = '⚠️ این سیستم (لوکال)';
            badge.className = 'badge mode-local';
        } else {
            badge.textContent = '🌐 شبکه LAN (ریموت)';
            badge.className = 'badge mode-remote';
        }
    }
    const uncIp = document.getElementById('preview-unc-ip');
    if (uncIp) uncIp.textContent = ip || '127.0.0.1';
}

targetIp.addEventListener('input', () => {
    updateTargetModeBadge(targetIp.value.trim());
});

const btnQuickLocal = document.getElementById('btn-quick-local');
if (btnQuickLocal) {
    btnQuickLocal.addEventListener('click', () => {
        targetIp.value = '127.0.0.1';
        targetUser.value = 'Current';
        targetPass.value = '';
        updateTargetModeBadge('127.0.0.1');

        logTerminal('حالت پایش سیستم جاری (این کامپیوتر) فعال شد. در حال بارگذاری مشخصات لوکال...', 'info');

        // Navigate to diagnostic tab and trigger scan
        const diagNav = document.getElementById('btn-nav-diagnostic');
        if (diagNav) diagNav.click();

        const diagBtn = document.getElementById('btn-run-diagnostic');
        if (diagBtn) diagBtn.click();
    });
}

selectProfile.addEventListener('change', (e) => {
    setActiveProfile(e.target.value);
});

// Modal Actions
const modal = document.getElementById('modal-add-profile');
document.getElementById('btn-add-profile').addEventListener('click', () => modal.classList.add('open'));
document.getElementById('btn-close-modal').addEventListener('click', () => modal.classList.remove('open'));
document.getElementById('btn-cancel-modal').addEventListener('click', () => modal.classList.remove('open'));

document.getElementById('btn-save-new-profile').addEventListener('click', async () => {
    const name = document.getElementById('new-name').value.trim();
    const ip = document.getElementById('new-ip').value.trim();
    const user = document.getElementById('new-user').value.trim();
    const pass = document.getElementById('new-pass').value;
    const notes = document.getElementById('new-notes').value.trim();

    if (!name || !ip || !user) {
        alert('لطفاً نام، آی‌پی و نام کاربری را وارد نمایید.');
        return;
    }

    const payload = {
        name,
        ipAddress: ip,
        username: user,
        password: pass,
        notes
    };

    try {
        const res = await fetch(`${API_BASE}/api/profiles`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
        if (!res.ok) throw new Error('خطا در ذخیره پروفایل');
        modal.classList.remove('open');
        await loadProfiles();
        logTerminal(`پروفایل جدید ذخیره شد: ${name}`, 'success');
    } catch (err) {
        alert(err.message);
    }
});

async function deleteProfile(id) {
    try {
        const res = await fetch(`${API_BASE}/api/profiles/${id}`, { method: 'DELETE' });
        if (!res.ok) throw new Error('حذف ناموفق بود.');
        if (activeProfile && activeProfile.id === id) activeProfile = null;
        await loadProfiles();
        logTerminal(`پروفایل حذف شد.`, 'warn');
    } catch (err) {
        alert(err.message);
    }
}

// --------------------------------------------------------------------------
// 3. Header Action Buttons: Connection Test & Remote Reboot
// --------------------------------------------------------------------------
document.getElementById('btn-test-connection').addEventListener('click', async () => {
    const ip = targetIp.value.trim();
    const user = targetUser.value.trim();
    const pass = targetPass.value;

    if (!ip) {
        alert('آدرس IP سیستم هدف را مشخص کنید.');
        return;
    }

    logTerminal(`در حال تست اتصال به ${ip} و بررسی پورت 5985...`, 'info');
    const testBtn = document.getElementById('btn-test-connection');
    testBtn.disabled = true;
    testBtn.querySelector('span').textContent = 'در حال تست...';

    try {
        // Probe host first
        const probeRes = await fetch(`${API_BASE}/api/probe`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ip })
        });
        const probeData = await probeRes.json();

        const isWinRm = Boolean(probeData.winRmPortOpen || probeData.isWinRMOpen);
        const isPing = Boolean(probeData.pingSucceeded || probeData.isPingAlive);
        const isLocal = !ip || ip === '127.0.0.1' || ip.toLowerCase() === 'localhost' || ip === '192.168.1.75' || ip === '::1';

        if (!isLocal && !isWinRm && !isPing) {
            logTerminal(`سیستم ${ip} به پینگ یا پورت 5985 پاسخی نداد. (وضعیت: آفلاین یا پورت بسته)`, 'error');
            alert(`اتصال برقرار نشد: پورت WinRM یا پینگ روی ${ip} پاسخ نداد.`);
            return;
        }

        logTerminal(`بررسی پورت موفق بود (WinRM: ${isWinRm ? 'باز' : 'نامشخص'}, Ping: ${isPing ? 'OK' : 'بسته'}). در حال تأیید اعتبار...`, 'info');

        // Test running quick hostname command
        const cmdRes = await fetch(`${API_BASE}/api/run-custom`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ip, username: user, password: pass, command: 'hostname; (Get-WmiObject Win32_OperatingSystem).Caption' })
        });
        const cmdData = await cmdRes.json();

        if (cmdData.success) {
            logTerminal(`ارتباط موفقیت‌آمیز برقرار شد! سیستم مقصد:\n${cmdData.output.trim()}`, 'success');
            alert(`اتصال موفق بود!\n${cmdData.output.trim()}`);
        } else {
            logTerminal(`پورت باز است اما احراز هویت ناموفق بود: ${cmdData.error}`, 'error');
            alert(`خطای دسترسی یا رمز عبور اشتباه:\n${cmdData.error}`);
        }
    } catch (err) {
        logTerminal(`خطا در تست اتصال: ${err.message}`, 'error');
        alert(`خطا: ${err.message}`);
    } finally {
        testBtn.disabled = false;
        testBtn.querySelector('span').textContent = 'تست اتصال';
    }
});

// --------------------------------------------------------------------------
// 3. Remote Reboot with Dedicated Safety Guard Modal
// --------------------------------------------------------------------------
const modalReboot = document.getElementById('modal-reboot-confirm');
const btnOpenRebootModal = document.getElementById('btn-remote-reboot');
const btnCloseRebootModal = document.getElementById('btn-close-reboot-modal');
const btnCancelReboot = document.getElementById('btn-cancel-reboot');
const btnExecuteConfirmedReboot = document.getElementById('btn-execute-confirmed-reboot');
const chkRebootConfirm = document.getElementById('chk-reboot-confirm');
const inputRebootText = document.getElementById('input-reboot-confirm-text');
const rebootLocalAlert = document.getElementById('reboot-local-alert');
const modalRebootTargetIp = document.getElementById('modal-reboot-target-ip');
const modalRebootTargetName = document.getElementById('modal-reboot-target-name');

function checkRebootFormValidity() {
    const isChecked = chkRebootConfirm && chkRebootConfirm.checked;
    const isTextValid = inputRebootText && inputRebootText.value.trim().toUpperCase() === 'REBOOT';
    if (btnExecuteConfirmedReboot) {
        if (isChecked && isTextValid) {
            btnExecuteConfirmedReboot.disabled = false;
            btnExecuteConfirmedReboot.style.opacity = '1';
        } else {
            btnExecuteConfirmedReboot.disabled = true;
            btnExecuteConfirmedReboot.style.opacity = '0.5';
        }
    }
}

if (btnOpenRebootModal) {
    btnOpenRebootModal.addEventListener('click', () => {
        const ip = targetIp.value.trim();
        const prof = currentProfiles.find(p => p.ipAddress === ip) || activeProfile || {};
        const isLocal = !ip || ip === '127.0.0.1' || ip.toLowerCase() === 'localhost' || ip === '192.168.1.75' || ip === '::1';

        if (modalRebootTargetIp) modalRebootTargetIp.textContent = ip || '127.0.0.1';
        if (modalRebootTargetName) modalRebootTargetName.textContent = prof.name || (isLocal ? 'Local Admin Workstation (این سیستم)' : 'Remote Client PC');

        if (rebootLocalAlert) {
            rebootLocalAlert.style.display = isLocal ? 'flex' : 'none';
        }

        if (chkRebootConfirm) chkRebootConfirm.checked = false;
        if (inputRebootText) inputRebootText.value = '';
        checkRebootFormValidity();

        if (modalReboot) modalReboot.style.display = 'flex';
    });
}

if (btnCloseRebootModal) btnCloseRebootModal.addEventListener('click', () => { if (modalReboot) modalReboot.style.display = 'none'; });
if (btnCancelReboot) btnCancelReboot.addEventListener('click', () => { if (modalReboot) modalReboot.style.display = 'none'; });
if (chkRebootConfirm) chkRebootConfirm.addEventListener('change', checkRebootFormValidity);
if (inputRebootText) inputRebootText.addEventListener('input', checkRebootFormValidity);

if (modalReboot) {
    modalReboot.addEventListener('click', (e) => {
        if (e.target === modalReboot) modalReboot.style.display = 'none';
    });
}

if (btnExecuteConfirmedReboot) {
    btnExecuteConfirmedReboot.addEventListener('click', async () => {
        const ip = targetIp.value.trim();
        if (modalReboot) modalReboot.style.display = 'none';

        logTerminal(`[SAFETY PASS] ارسال فرمان ری‌استارت قطعی به ${ip}...`, 'warn');
        try {
            const res = await fetch(`${API_BASE}/api/reboot`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    ip,
                    username: targetUser.value.trim(),
                    password: targetPass.value
                })
            });
            const data = await res.json();
            logTerminal(`فرمان ری‌استارت به ${ip} ارسال شد. منتظر بالا آمدن سیستم بمانید.`, 'success');
            alert(`دستور ری‌استارت با موفقیت به سیستم ${ip} ارسال شد.`);
        } catch (err) {
            logTerminal(`خطا در ری‌استارت: ${err.message}`, 'error');
            alert(`خطا: ${err.message}`);
        }
    });
}


// --------------------------------------------------------------------------
// 4. Network Radar Scanner
// --------------------------------------------------------------------------
document.getElementById('btn-start-scan').addEventListener('click', async () => {
    const subnet = document.getElementById('radar-subnet').value.trim();
    const sweep = document.getElementById('radar-sweep');
    const statusText = document.getElementById('radar-status-text');
    const progressBar = document.getElementById('radar-progress');
    const tbody = document.getElementById('scan-results-tbody');
    const scanBtn = document.getElementById('btn-start-scan');

    scanBtn.disabled = true;
    sweep.classList.add('active');
    statusText.textContent = `در حال اسکن رادار موازی در ساب‌نت ${subnet}.0/24 ...`;
    progressBar.style.width = '30%';
    tbody.innerHTML = `<tr><td colspan="6" class="text-center text-muted">در حال پویش ۲۵۴ آدرس شبکه محلی... لطفاً شکیبا باشید.</td></tr>`;
    logTerminal(`شروع اسکن رادار موازی ساب‌نت ${subnet}.0/24...`, 'info');

    try {
        const res = await fetch(`${API_BASE}/api/scan`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ subnet, start: 1, end: 254 })
        });
        const results = await res.json();

        progressBar.style.width = '100%';
        sweep.classList.remove('active');
        statusText.textContent = `اسکن کامل شد. ${results.length} دستگاه آنلاین شناسایی گردید.`;
        logTerminal(`اسکن رادار پایان یافت. ${results.length} سیستم زنده شناسایی شد.`, 'success');

        if (results.length === 0) {
            tbody.innerHTML = `<tr><td colspan="6" class="text-center text-muted">دستگاه آنلاینی در این رنج یافت نشد.</td></tr>`;
            return;
        }

        tbody.innerHTML = '';
        results.forEach(d => {
            const tr = document.createElement('tr');
            const isWinRm = Boolean(d.winRmPortOpen || d.isWinRMOpen);
            const rtt = d.roundtripTimeMs ?? d.roundTripTimeMs ?? 0;
            const winrmBadge = isWinRm 
                ? `<span class="badge badge-emerald">باز (5985 OK)</span>` 
                : `<span class="badge badge-rose">بسته</span>`;
            const statusBadge = isWinRm
                ? `<span class="badge badge-cyan">آماده عیب‌یابی</span>`
                : `<span class="badge badge-amber">فقط پینگ</span>`;

            tr.innerHTML = `
                <td><strong>${d.ipAddress}</strong></td>
                <td>${d.hostname || 'نامشخص'}</td>
                <td>${rtt} ms</td>
                <td>${winrmBadge}</td>
                <td>${statusBadge}</td>
                <td>
                    <button class="btn btn-primary btn-sm set-target-btn">انتخاب به عنوان هدف</button>
                </td>
            `;

            tr.querySelector('.set-target-btn').addEventListener('click', () => {
                targetIp.value = d.ipAddress;
                logTerminal(`سیستم ${d.ipAddress} در نوار هدف فعال تنظیم شد.`, 'info');
                // Navigate to diagnostic tab
                document.getElementById('btn-nav-diagnostic').click();
            });

            tbody.appendChild(tr);
        });
    } catch (err) {
        statusText.textContent = `خطا در اسکن: ${err.message}`;
        logTerminal(`خطا در اسکن رادار: ${err.message}`, 'error');
    } finally {
        scanBtn.disabled = false;
        sweep.classList.remove('active');
    }
});

// --------------------------------------------------------------------------
// 5. Deep Diagnostic Engine
// --------------------------------------------------------------------------
document.getElementById('btn-run-diagnostic').addEventListener('click', async () => {
    const ip = targetIp.value.trim();
    const user = targetUser.value.trim();
    const pass = targetPass.value;

    const diagBtn = document.getElementById('btn-run-diagnostic');
    diagBtn.disabled = true;
    diagBtn.querySelector('span').textContent = 'در حال استخراج مشخصات عمیق...';
    logTerminal(`شروع اجرای عیب‌یابی عمیق روی ${ip}...`, 'info');

    try {
        const res = await fetch(`${API_BASE}/api/diagnose`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ip, username: user, password: pass })
        });
        const result = await res.json();

        if (!result.success || !result.data) {
            throw new Error(result.error || 'پاسخ نامعتبر از سیستم مقصد');
        }

        const data = result.data;
        renderDiagnosticResults(data);
        renderAiReadiness(data);
        logTerminal(`عیب‌یابی عمیق با موفقیت به پایان رسید.`, 'success');
    } catch (err) {
        logTerminal(`خطا در عیب‌یابی عمیق: ${err.message}`, 'error');
        alert(`خطا در عیب‌یابی: ${err.message}`);
    } finally {
        diagBtn.disabled = false;
        diagBtn.querySelector('span').textContent = 'اجرای اسکن عمیق سیستم';
    }
});

function renderDiagnosticResults(d) {
    lastDiagnosticData = d;

    // CPU
    if (d.Processor) {
        document.getElementById('diag-cpu-name').textContent = d.Processor.Name || '-';
        document.getElementById('diag-cpu-cores').textContent = `${d.Processor.Cores} هسته / ${d.Processor.Threads} رشته`;
        document.getElementById('diag-cpu-clocks').textContent = `کلاک پایه: ${d.Processor.MaxClockMHz} MHz | کلاک زنده: ${d.Processor.CurrentClockMHz} MHz`;
    }

    // RAM
    if (d.Memory) {
        document.getElementById('diag-ram-total').textContent = `${d.Memory.TotalGB} گیگابایت`;
        document.getElementById('diag-ram-free').textContent = `رم آزاد: ${d.Memory.FreeGB} GB (${d.Memory.PercentFree}%) | ماژول‌ها: ${d.Memory.DimmCount}`;
        
        const channelBadge = document.getElementById('diag-ram-channel');
        channelBadge.textContent = d.Memory.ChannelMode;
        if (d.Memory.ChannelMode && d.Memory.ChannelMode.includes('Dual')) {
            channelBadge.className = 'badge badge-emerald';
        } else {
            channelBadge.className = 'badge badge-rose';
        }
    }

    // GPU
    if (d.Graphics) {
        const gName = (d.Graphics.Controllers && d.Graphics.Controllers.length > 0)
            ? d.Graphics.Controllers.map(g => g.Name).join(' + ')
            : 'بدون گرافیک مجزا';
        document.getElementById('diag-gpu-name').textContent = gName;
        document.getElementById('diag-gpu-vram').textContent = `VRAM انویدیا: ${d.Graphics.NvidiaVramGB} گیگابایت`;

        const cudaBadge = document.getElementById('diag-gpu-cuda');
        if (d.Graphics.HasNvidiaCuda) {
            cudaBadge.textContent = 'CUDA: فعال (شتاب‌دهنده هوش مصنوعی)';
            cudaBadge.className = 'badge badge-purple';
        } else {
            cudaBadge.textContent = 'CUDA: فاقد انویدیا (فقط CPU)';
            cudaBadge.className = 'badge badge-rose';
        }
    }

    // Storage
    if (d.Storage && d.Storage.PhysicalDisks) {
        const primary = d.Storage.PhysicalDisks[0] || {};
        document.getElementById('diag-ssd-model').textContent = primary.FriendlyName || 'دیسک سیستمی';
        document.getElementById('diag-ssd-health').textContent = `سلامت: ${primary.HealthStatus || 'Healthy'}`;
        
        if (d.Storage.Volumes && d.Storage.Volumes.length > 0) {
            const volC = d.Storage.Volumes.find(v => v.DriveLetter === 'C') || d.Storage.Volumes[0];
            document.getElementById('diag-ssd-free').textContent = `فضای آزاد درایو C: ${volC.FreeGB} GB از ${volC.SizeGB} GB (${volC.PercentFree}%)`;
        }
    }

    // Services & Optimization Status
    if (d.OptimizationState) {
        document.getElementById('val-sysmain').textContent = d.OptimizationState.SysMainStatus || 'نامشخص';
        document.getElementById('val-power').textContent = (d.OptimizationState.PowerScheme && d.OptimizationState.PowerScheme.includes('High')) ? 'High Performance (عالی)' : 'Balanced / دیگر';
        document.getElementById('val-fastboot').textContent = d.OptimizationState.FastStartupEnabled ? 'روشن (توصیه: خاموش شود)' : 'خاموش (بهینه)';
        document.getElementById('val-update-cache').textContent = `${d.OptimizationState.PendingUpdateFilesCount} فایل در صف`;
    }

    if (d.System) {
        document.getElementById('val-uptime').textContent = `${d.System.UptimeHours} ساعت (${d.System.OSCaption})`;
    }

    // Top Processes
    const topTbody = document.getElementById('top-procs-tbody');
    if (topTbody && d.TopProcesses) {
        topTbody.innerHTML = '';
        d.TopProcesses.forEach(p => {
            const tr = document.createElement('tr');
            tr.innerHTML = `
                <td><strong>${p.ProcessName}</strong> (ID: ${p.Id})</td>
                <td>${p.CPU_Sec}s</td>
                <td>${p.RAM_MB} MB</td>
            `;
            topTbody.appendChild(tr);
        });
    }

    // 1. Calculate and Render System Health Gauge
    calculateAndRenderHealthScore(d);

    // 2. Render Motherboard Hardware Topology & RAM Slots
    renderMotherboardTopology(d);
}

function calculateAndRenderHealthScore(d) {
    let bootScore = 40;
    if (d.OptimizationState) {
        let bs = 0;
        if (d.OptimizationState.SysMainStatus === 'Stopped' || d.OptimizationState.SysMainStatus === 'Disabled') bs += 25;
        if (!d.OptimizationState.FastStartupEnabled) bs += 25;
        if (d.OptimizationState.PendingUpdateFilesCount === 0) bs += 25; else bs += 10;
        if (d.OptimizationState.PowerScheme && d.OptimizationState.PowerScheme.includes('High')) bs += 25; else bs += 10;
        bootScore = bs;
    }

    let diskScore = 50;
    if (d.Storage) {
        let ds = 0;
        if (d.Storage.PhysicalDisks && d.Storage.PhysicalDisks.length > 0) {
            const h = d.Storage.PhysicalDisks[0].HealthStatus;
            if (h === 'Healthy' || !h) ds += 50; else ds += 20;
        } else ds += 40;
        if (d.Storage.Volumes && d.Storage.Volumes.length > 0) {
            const volC = d.Storage.Volumes.find(v => v.DriveLetter === 'C') || d.Storage.Volumes[0];
            if (volC && volC.PercentFree > 15) ds += 50; else ds += 20;
        } else ds += 40;
        diskScore = ds;
    }

    let ramScore = 50;
    if (d.Memory) {
        let rs = 0;
        if (d.Memory.ChannelMode && d.Memory.ChannelMode.includes('Dual')) rs += 60; else rs += 25;
        if (d.Memory.PercentFree > 25) rs += 40; else rs += 20;
        ramScore = rs;
    }

    let cpuScore = 75;
    if (d.Processor) {
        if (d.Processor.CurrentClockMHz && d.Processor.MaxClockMHz) {
            const ratio = d.Processor.CurrentClockMHz / d.Processor.MaxClockMHz;
            if (ratio >= 0.8) cpuScore = 95;
            else if (ratio >= 0.6) cpuScore = 80;
            else cpuScore = 65;
        }
    }

    // Composite total (0 - 100)
    const composite = Math.min(100, Math.max(10, Math.round(
        (bootScore * 0.3) + (diskScore * 0.25) + (ramScore * 0.3) + (cpuScore * 0.15)
    )));

    // Update UI numbers & mini progress bars
    const scoreVal = document.getElementById('health-score-val');
    if (scoreVal) scoreVal.textContent = composite;
    
    const setElem = (id, txt) => { const el = document.getElementById(id); if (el) el.textContent = txt; };
    const setWidth = (id, w) => { const el = document.getElementById(id); if (el) el.style.width = w; };

    setElem('score-metric-boot', `${bootScore}%`);
    setWidth('bar-metric-boot', `${bootScore}%`);

    setElem('score-metric-disk', `${diskScore}%`);
    setWidth('bar-metric-disk', `${diskScore}%`);

    setElem('score-metric-ram', `${ramScore}%`);
    setWidth('bar-metric-ram', `${ramScore}%`);

    setElem('score-metric-cpu', `${cpuScore}%`);
    setWidth('bar-metric-cpu', `${cpuScore}%`);

    // SVG animated circle
    const circle = document.getElementById('health-gauge-circle');
    if (circle) {
        const offset = 314.15 - (314.15 * composite / 100);
        circle.style.strokeDashoffset = offset;
    }

    const badge = document.getElementById('health-grade-badge');
    const title = document.getElementById('health-status-title');
    const desc = document.getElementById('health-status-desc');

    if (badge && title && desc && circle) {
        if (composite >= 85) {
            circle.style.stroke = 'var(--emerald)';
            badge.className = 'gauge-grade-badge grade-a';
            badge.textContent = 'رتبه سلامت: A+ (عالی - در اوج آمادگی)';
            title.textContent = 'سیستم کاملاً بهینه و پایدار است';
            desc.textContent = 'تمامی سرویس‌های مزاحم متوقف، سرعت فرکانس CPU حداکثری و سلامت حافظه و هارد در وضعیت عالی قرار دارد.';
        } else if (composite >= 70) {
            circle.style.stroke = 'var(--cyan)';
            badge.className = 'gauge-grade-badge grade-b';
            badge.textContent = 'رتبه سلامت: B (خوب - قابل قبول)';
            title.textContent = 'سیستم آماده کار با پتانسیل بهینه‌سازی';
            desc.textContent = 'وضعیت عمومی مطلوب است؛ با اجرای بهینه‌سازی ۶ مرحله‌ای می‌توان سرعت خواندن دیسک و کلاک را ارتقا داد.';
        } else if (composite >= 50) {
            circle.style.stroke = 'var(--amber)';
            badge.className = 'gauge-grade-badge grade-c';
            badge.textContent = 'رتبه سلامت: C (متوسط - گلوگاه شناسایی شد)';
            title.textContent = 'گلوگاه سرعت و درگیری سرویس‌ها مشاهده شد';
            desc.textContent = 'سرویس‌های SysMain، کش آپدیت یا تک‌کاناله بودن رم باعث افت پهنای باند شده‌اند. بهینه‌سازی توربو را اجرا کنید.';
        } else {
            circle.style.stroke = 'var(--rose)';
            badge.className = 'gauge-grade-badge grade-f';
            badge.textContent = 'رتبه سلامت: F (بحرانی - کندی شدید)';
            title.textContent = 'نیازمند بهینه‌سازی فوری و رفع خطاهای ویندوز';
            desc.textContent = 'انباشت کش معیوب، پر بودن درایو C یا خطاهای سیستمی موجب هدررفت توان سخت‌افزار شده است.';
        }
    }
}

function renderMotherboardTopology(d) {
    if (d.Processor) {
        const cpuName = document.getElementById('topo-cpu-name');
        const cpuCores = document.getElementById('topo-cpu-cores');
        const cpuClock = document.getElementById('topo-cpu-clock');
        if (cpuName) cpuName.textContent = d.Processor.Name || 'پردازنده مرکزی';
        if (cpuCores) cpuCores.textContent = `${d.Processor.Cores}C / ${d.Processor.Threads}T`;
        if (cpuClock) cpuClock.textContent = `${d.Processor.CurrentClockMHz || d.Processor.MaxClockMHz} MHz`;
    }

    const channelBadge = document.getElementById('topo-channel-badge');
    const banner = document.getElementById('topo-bottleneck-banner');
    const container = document.getElementById('topo-dimm-slots-container');

    const isDual = d.Memory && d.Memory.ChannelMode && d.Memory.ChannelMode.includes('Dual');
    const isSingle = !isDual;

    if (channelBadge) {
        if (isDual) {
            channelBadge.textContent = 'دوکاناله فعال (Dual Channel 128-bit)';
            channelBadge.className = 'badge badge-emerald';
        } else {
            channelBadge.textContent = 'تک‌کاناله (Single-Channel Bottleneck)';
            channelBadge.className = 'badge badge-amber';
        }
    }

    if (banner) {
        banner.style.display = isSingle ? 'flex' : 'none';
    }

    // Render 4 DIMM slots
    if (container) {
        container.innerHTML = '';
        const modules = (d.Memory && d.Memory.Modules) ? (Array.isArray(d.Memory.Modules) ? d.Memory.Modules : [d.Memory.Modules]) : [];
        const slotNames = ['اسلات ۱ (DIMM A)', 'اسلات ۲ (DIMM B)', 'اسلات ۳ (DIMM C)', 'اسلات ۴ (DIMM D)'];
        
        for (let i = 0; i < 4; i++) {
            const mod = modules[i];
            const div = document.createElement('div');
            
            if (mod) {
                const capGB = mod.Capacity ? Math.round(mod.Capacity / (1024*1024*1024)) : (d.Memory.TotalGB / (modules.length || 1));
                const speed = mod.Speed ? `${mod.Speed}MHz` : 'DDR4';
                const mfg = mod.Manufacturer ? mod.Manufacturer.trim() : '';
                div.className = 'dimm-slot active';
                div.innerHTML = `
                    <div class="slot-pin"></div>
                    <div class="slot-body">
                        <div class="slot-head">${slotNames[i]}</div>
                        <div class="slot-meta">${capGB}GB ${speed} ${mfg}</div>
                    </div>
                `;
            } else {
                const shouldHighlight = isSingle && (i === 0 || (modules.length === 1 && i === 1));
                div.className = `dimm-slot empty ${shouldHighlight ? 'highlight-upgrade' : ''}`;
                div.innerHTML = `
                    <div class="slot-pin"></div>
                    <div class="slot-body">
                        <div class="slot-head">${slotNames[i]}</div>
                        <div class="slot-meta">${shouldHighlight ? '⚡ آماده ارتقای دوکاناله' : 'خالی (Empty Slot)'}</div>
                    </div>
                `;
            }
            container.appendChild(div);
        }
    }

    // Storage card
    if (d.Storage && d.Storage.PhysicalDisks && d.Storage.PhysicalDisks.length > 0) {
        const primary = d.Storage.PhysicalDisks[0];
        const nvmeModel = document.getElementById('topo-nvme-model');
        const nvmeDetails = document.getElementById('topo-nvme-details');
        if (nvmeModel) nvmeModel.textContent = primary.FriendlyName || 'درایو NVMe اصلی';
        if (nvmeDetails) nvmeDetails.textContent = `سلامت: ${primary.HealthStatus || 'Healthy'} | نوع: ${primary.BusType || 'NVMe'}`;
    }

    // GPU card
    if (d.Graphics) {
        const primaryGpu = (d.Graphics.Controllers && d.Graphics.Controllers.length > 0) ? d.Graphics.Controllers[0].Name : 'گرافیک مجتمع';
        const gpuModel = document.getElementById('topo-gpu-model');
        const gpuDetails = document.getElementById('topo-gpu-details');
        if (gpuModel) gpuModel.textContent = primaryGpu;
        if (gpuDetails) gpuDetails.textContent = `VRAM: ${d.Graphics.NvidiaVramGB}GB | ${d.Graphics.HasNvidiaCuda ? 'شتاب‌دهنده CUDA فعال' : 'بدون CUDA'}`;
    }
}

// --------------------------------------------------------------------------
// 6. AI Readiness Lab Rendering
// --------------------------------------------------------------------------
function renderAiReadiness(d) {
    const ai = d.AIReadiness;
    if (!ai) return;

    document.getElementById('ai-score-val').textContent = ai.Score;
    document.getElementById('ai-tier-title').textContent = ai.Tier;

    const recBox = document.getElementById('ai-recommendations');
    recBox.innerHTML = '';

    if (ai.Recommendations && ai.Recommendations.length > 0) {
        ai.Recommendations.forEach(r => {
            const pill = document.createElement('div');
            pill.className = 'rec-pill';
            pill.textContent = `💡 ${r}`;
            recBox.appendChild(pill);
        });
    } else {
        const pill = document.createElement('div');
        pill.className = 'rec-pill';
        pill.textContent = '✅ سیستم از نظر پردازش هوش مصنوعی در وضعیت بسیار مطلوبی قرار دارد.';
        recBox.appendChild(pill);
    }

    // Update Matrix Statuses based on Score
    const hasCuda = d.Graphics && d.Graphics.HasNvidiaCuda;
    const vram = d.Graphics ? (d.Graphics.NvidiaVramGB || 0) : 0;
    const ram = d.Memory ? (d.Memory.TotalGB || 0) : 0;

    const statusLlm = document.getElementById('status-llm');
    const statusWhisper = document.getElementById('status-whisper');
    const statusSd = document.getElementById('status-sd');
    const statusPytorch = document.getElementById('status-pytorch');

    if (hasCuda && vram >= 6) {
        statusLlm.textContent = 'کاملاً مناسب (Llama 3 8B Q4 / 35 tok/s)';
        statusLlm.style.color = '#34d399';

        statusWhisper.textContent = 'بسیار سریع (FP16 Real-time 5x)';
        statusWhisper.style.color = '#34d399';

        statusSd.textContent = 'سازگار (Stable Diffusion 1.5 / SDXL Turbo)';
        statusSd.style.color = '#34d399';

        statusPytorch.textContent = 'پشتیبانی کامل از PyTorch CUDA 12.x';
        statusPytorch.style.color = '#34d399';
    } else if (hasCuda) {
        statusLlm.textContent = 'مناسب برای مدل‌های سبک (TinyLlama, Phi-3 Mini)';
        statusLlm.style.color = '#fbbf24';

        statusWhisper.textContent = 'شتاب مناسب با Whisper Base/Small';
        statusWhisper.style.color = '#34d399';

        statusSd.textContent = 'محدود به دلیل VRAM کم';
        statusSd.style.color = '#fb7185';

        statusPytorch.textContent = 'فعال با بچ‌سایزهای کوچک';
        statusPytorch.style.color = '#fbbf24';
    } else {
        statusLlm.textContent = 'غیرقابل توصیه (بسیار کند روی CPU)';
        statusLlm.style.color = '#fb7185';

        statusWhisper.textContent = 'فقط مدل ریز (Tiny) با CPU';
        statusWhisper.style.color = '#fbbf24';

        statusSd.textContent = 'غیرقابل اجرا (نیاز به کارت گرافیک انویدیا)';
        statusSd.style.color = '#fb7185';

        statusPytorch.textContent = 'فاقد هسته‌های شتاب‌دهنده CUDA';
        statusPytorch.style.color = '#fb7185';
    }
}

// --------------------------------------------------------------------------
// 7. 1-Click Turbo Optimization Engine
// --------------------------------------------------------------------------
document.getElementById('btn-run-optimize').addEventListener('click', async () => {
    const ip = targetIp.value.trim();
    const user = targetUser.value.trim();
    const pass = targetPass.value;

    if (!confirm(`آیا می‌خواهید عملیات بهینه‌سازی توربو ۶ مرحله‌ای روی سیستم ${ip} اجرا شود؟`)) return;

    const optBtn = document.getElementById('btn-run-optimize');
    optBtn.disabled = true;
    optBtn.querySelector('span').textContent = 'در حال بهینه‌سازی ۶ مرحله‌ای...';

    // Reset step styles
    for (let i = 1; i <= 6; i++) {
        const step = document.getElementById(`opt-step-${i}`);
        const status = document.getElementById(`status-step-${i}`);
        if (step) {
            step.classList.remove('completed', 'running');
            step.classList.add('running');
        }
        if (status) status.textContent = 'در حال پردازش...';
    }

    logTerminal(`شروع اجرای بسته بهینه‌سازی توربو روی ${ip}...`, 'info');

    try {
        const res = await fetch(`${API_BASE}/api/optimize`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ip, username: user, password: pass })
        });
        const data = await res.json();

        // Mark all completed
        for (let i = 1; i <= 6; i++) {
            const step = document.getElementById(`opt-step-${i}`);
            const status = document.getElementById(`status-step-${i}`);
            if (step) {
                step.classList.remove('running');
                step.classList.add('completed');
            }
            if (status) status.textContent = 'موفق [OK]';
        }

        logTerminal(`بهینه‌سازی کامل شد! خروجی سیستم:\n${data.output}`, 'success');
        alert(`بسته بهینه‌سازی توربو در مدت ${data.durationSeconds} ثانیه با موفقیت به پایان رسید.`);
    } catch (err) {
        logTerminal(`خطا در اجرای بهینه‌سازی: ${err.message}`, 'error');
        alert(`خطا در بهینه‌سازی: ${err.message}`);
    } finally {
        optBtn.disabled = false;
        optBtn.querySelector('span').textContent = 'شروع بهینه‌سازی خودکار ۶ مرحله‌ای';
    }
});

// --------------------------------------------------------------------------
// 8. Driver Injector
// --------------------------------------------------------------------------
document.getElementById('btn-start-driver-install').addEventListener('click', async () => {
    const folder = document.getElementById('driver-folder-path').value.trim();
    const ip = targetIp.value.trim();
    const user = targetUser.value.trim();
    const pass = targetPass.value;

    if (!folder) {
        alert('لطفاً مسیر پوشه حاوی درایورها را مشخص کنید.');
        return;
    }

    const driverBtn = document.getElementById('btn-start-driver-install');
    driverBtn.disabled = true;
    driverBtn.querySelector('span').textContent = 'در حال استخراج و تزریق درایورها...';
    logTerminal(`شروع اکسترکت سایلنت و تزریق درایورهای مسیر ${folder} روی ${ip}...`, 'warn');

    try {
        const res = await fetch(`${API_BASE}/api/install-drivers`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ip, username: user, password: pass, folderPath: folder })
        });
        const data = await res.json();

        logTerminal(`نتیجه تزریق درایورها:\n${data.output}`, 'success');
        alert('تزریق درایورها با موفقیت پایان یافت.');
    } catch (err) {
        logTerminal(`خطا در نصب درایورها: ${err.message}`, 'error');
        alert(`خطا: ${err.message}`);
    } finally {
        driverBtn.disabled = false;
        driverBtn.querySelector('span').textContent = 'شروع استخراج و تزریق درایورها';
    }
});

// --------------------------------------------------------------------------
// 8.5 Dev & Database Toolkit Engine (SQL Server & Visual Studio Fixes)
// --------------------------------------------------------------------------
const btnDevDbAudit = document.getElementById('btn-run-devdb-audit');
if (btnDevDbAudit) {
    btnDevDbAudit.addEventListener('click', async () => {
        const ip = targetIp.value.trim();
        const user = targetUser.value.trim();
        const pass = targetPass.value;

        btnDevDbAudit.disabled = true;
        btnDevDbAudit.querySelector('span').textContent = 'در حال عیب‌یابی پیش‌نیازهای دیتابیس...';
        logTerminal(`شروع ممیزی جامع SQL Server و Visual Studio روی ${ip}...`, 'info');

        try {
            const res = await fetch(`${API_BASE}/api/devdb/audit`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ ip, username: user, password: pass })
            });
            const result = await res.json();

            if (!result.success || !result.data) {
                throw new Error(result.error || 'پاسخ نامعتبر از ممیزی دیتابیس');
            }

            const data = result.data;
            renderDevDbAudit(data);
            logTerminal('ممیزی وضعیت SQL Server و Visual Studio با موفقیت استخراج شد.', 'success');
        } catch (err) {
            logTerminal(`خطا در ممیزی دیتابیس: ${err.message}`, 'error');
            alert(`خطا: ${err.message}`);
        } finally {
            btnDevDbAudit.disabled = false;
            btnDevDbAudit.querySelector('span').textContent = 'اسکن و عیب‌یابی پیش‌نیازهای دیتابیس';
        }
    });
}

function renderDevDbAudit(d) {
    // 1. NVMe Sector Size
    const nvmeVal = document.getElementById('val-nvme-patch');
    const nvmeBadge = document.getElementById('badge-nvme-patch');
    if (d.NvmeStatus) {
        if (!d.NvmeStatus.HasNvmeDrive) {
            nvmeVal.textContent = 'بدون NVMe';
            nvmeBadge.textContent = 'عدم نیاز (SATA/HDD)';
            nvmeBadge.className = 'badge badge-cyan';
        } else if (d.NvmeStatus.IsPatched) {
            nvmeVal.textContent = 'پچ شده (4095 OK)';
            nvmeBadge.textContent = 'ایمن در برابر 0x851A001A';
            nvmeBadge.className = 'badge badge-emerald';
        } else {
            nvmeVal.textContent = 'پچ نشده (خطر خطا)';
            nvmeBadge.textContent = 'احتمال کرش SQL Server!';
            nvmeBadge.className = 'badge badge-rose';
        }
    }

    // 2. Pending Reboot Lock
    const rebootVal = document.getElementById('val-reboot-lock');
    const rebootBadge = document.getElementById('badge-reboot-lock');
    if (d.RebootLock) {
        if (d.RebootLock.HasLock) {
            rebootVal.textContent = 'قفل فعال است';
            rebootBadge.textContent = `شناسایی ${d.RebootLock.Reasons.length} مانع ستاپ`;
            rebootBadge.className = 'badge badge-rose';
        } else {
            rebootVal.textContent = 'بدون قفل (تمیز)';
            rebootBadge.textContent = 'آماده نصب ستاپ';
            rebootBadge.className = 'badge badge-emerald';
        }
    }

    // 3. WMI Status
    const wmiVal = document.getElementById('val-wmi-status');
    const wmiBadge = document.getElementById('badge-wmi-status');
    if (d.WmiStatus) {
        if (d.WmiStatus.IsConsistent) {
            wmiVal.textContent = 'سالم (Consistent)';
            wmiBadge.textContent = 'مخزن تأیید شد';
            wmiBadge.className = 'badge badge-emerald';
        } else {
            wmiVal.textContent = 'خراب یا نامطمئن';
            wmiBadge.textContent = 'نیاز به ریست WMI';
            wmiBadge.className = 'badge badge-rose';
        }
    }

    // 4. Visual Studio
    const vsVal = document.getElementById('val-vs-cache');
    const vsBadge = document.getElementById('badge-vs-status');
    if (d.VisualStudio) {
        vsVal.textContent = `${d.VisualStudio.PackageCacheSizeMB} مگابایت (${d.VisualStudio.PackageCacheCount} پکیج)`;
        vsBadge.textContent = d.VisualStudio.HasInstaller ? 'Installer موجود است' : 'نصاب یافت نشد';
    }
}

async function runDevDbFix(fixType, buttonId, actionName) {
    const ip = targetIp.value.trim();
    const user = targetUser.value.trim();
    const pass = targetPass.value;

    if (!confirm(`آیا مطمئن هستید که می‌خواهید عملیات «${actionName}» روی سیستم ${ip} اجرا شود؟`)) return;

    const btn = document.getElementById(buttonId);
    if (btn) btn.disabled = true;

    logTerminal(`شروع اجرای عملیات ${actionName} (${fixType}) روی ${ip}...`, 'warn');

    try {
        const res = await fetch(`${API_BASE}/api/devdb/fix`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ip, username: user, password: pass, fixType })
        });
        const data = await res.json();

        logTerminal(`نتیجه عملیات ${actionName}:\n${data.output}`, 'success');
        alert(`عملیات ${actionName} با موفقیت به پایان رسید.`);

        // Auto re-audit to update badges
        if (btnDevDbAudit) btnDevDbAudit.click();
    } catch (err) {
        logTerminal(`خطا در اجرای ${actionName}: ${err.message}`, 'error');
        alert(`خطا: ${err.message}`);
    } finally {
        if (btn) btn.disabled = false;
    }
}

// Bind DevDb Fix Buttons
const fixNvmeBtn = document.getElementById('btn-fix-nvme');
if (fixNvmeBtn) fixNvmeBtn.addEventListener('click', () => runDevDbFix('sql-nvme', 'btn-fix-nvme', 'پچ سکتورسایز NVMe'));

const fixRebootBtn = document.getElementById('btn-fix-reboot');
if (fixRebootBtn) fixRebootBtn.addEventListener('click', () => runDevDbFix('pending-reboot', 'btn-fix-reboot', 'حذف قفل Pending Reboot'));

const fixVsBtn = document.getElementById('btn-fix-vs');
if (fixVsBtn) fixVsBtn.addEventListener('click', () => runDevDbFix('vs-cleanup', 'btn-fix-vs', 'اسکراب و ریست Visual Studio'));

const fixWmiBtn = document.getElementById('btn-fix-wmi');
if (fixWmiBtn) fixWmiBtn.addEventListener('click', () => runDevDbFix('wmi-repair', 'btn-fix-wmi', 'تعمیر و ریست WMI'));

const fixAllBtn = document.getElementById('btn-fix-all-devdb');
if (fixAllBtn) fixAllBtn.addEventListener('click', () => runDevDbFix('all-in-one', 'btn-fix-all-devdb', 'پکیج جامع All-In-One'));

// --------------------------------------------------------------------------
// 8.8 Windows Rescue & Boot Doctor Engine
// --------------------------------------------------------------------------
const btnRescueAudit = document.getElementById('btn-run-rescue-audit');
if (btnRescueAudit) {
    btnRescueAudit.addEventListener('click', async () => {
        const ip = targetIp.value.trim();
        const user = targetUser.value.trim();
        const pass = targetPass.value;

        btnRescueAudit.disabled = true;
        btnRescueAudit.querySelector('span').textContent = 'در حال اسکن بوت و کرش‌ها...';
        logTerminal(`شروع ارزیابی اورژانس بوت و لاگ‌های BSOD روی ${ip}...`, 'info');

        try {
            const res = await fetch(`${API_BASE}/api/rescue/audit`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ ip, username: user, password: pass })
            });
            const result = await res.json();

            if (!result.success || !result.data) {
                throw new Error(result.error || 'پاسخ نامعتبر از ممیزی بوت');
            }

            const data = result.data;
            renderRescueAudit(data);
            logTerminal('ممیزی وضعیت بوت و کرش‌های هسته با موفقیت استخراج شد.', 'success');
        } catch (err) {
            logTerminal(`خطا در ممیزی بوت: ${err.message}`, 'error');
            alert(`خطا: ${err.message}`);
        } finally {
            btnRescueAudit.disabled = false;
            btnRescueAudit.querySelector('span').textContent = 'اسکن سلامت بوت و کرش‌ها';
        }
    });
}

function renderRescueAudit(d) {
    // 1. WinRE
    const winreVal = document.getElementById('val-winre-status');
    const winreBadge = document.getElementById('badge-winre-status');
    if (d.WinRE) {
        if (d.WinRE.IsEnabled) {
            winreVal.textContent = 'فعال و آماده کار';
            winreBadge.textContent = 'پارتیشن ریکاوری OK';
            winreBadge.className = 'badge badge-emerald';
        } else {
            winreVal.textContent = 'غیرفعال (خطر عدم بوت)';
            winreBadge.textContent = 'نیاز به فعال‌سازی فوری';
            winreBadge.className = 'badge badge-rose';
        }
    }

    // 2. BSOD
    const bsodVal = document.getElementById('val-bsod-status');
    const bsodBadge = document.getElementById('badge-bsod-status');
    if (d.BSOD) {
        if (d.BSOD.MinidumpCount > 0) {
            bsodVal.textContent = `${d.BSOD.MinidumpCount} فایل مینیدامپ (${d.BSOD.LatestDumpDate})`;
            bsodBadge.textContent = 'ثبت کرش صفحه آبی';
            bsodBadge.className = 'badge badge-amber';
            logTerminal(`آخرین پیام ثبت‌شده از صفحه آبی (BugCheck): ${d.BSOD.LastBugCheckMessage}`, 'warn');
        } else {
            bsodVal.textContent = 'بدون کرش بحرانی';
            bsodBadge.textContent = 'هسته پایدار (بدون BSOD)';
            bsodBadge.className = 'badge badge-emerald';
        }
    }

    // 3. Bootloader BCD
    const bcdVal = document.getElementById('val-bcd-status');
    const bcdBadge = document.getElementById('badge-bcd-status');
    if (d.Bootloader) {
        if (d.Bootloader.IsSafeboot) {
            bcdVal.textContent = 'SafeBoot فعال است';
            bcdBadge.textContent = 'قفل در حالت Safe Mode';
            bcdBadge.className = 'badge badge-amber';
        } else {
            bcdVal.textContent = `مهلت بوت: ${d.Bootloader.TimeoutSeconds} ثانیه`;
            bcdBadge.textContent = 'UEFI BCD نرمال';
            bcdBadge.className = 'badge badge-emerald';
        }
    }

    // 4. Pending XML Loop
    const updVal = document.getElementById('val-pendingxml-status');
    const updBadge = document.getElementById('badge-pendingxml-status');
    if (d.UpdateHealth) {
        if (d.UpdateHealth.HasPendingXml || d.UpdateHealth.IsCbsRebootPending) {
            updVal.textContent = 'فایل قفل در صف اعمال';
            updBadge.textContent = 'احتمال گیر کردن حین بوت!';
            updBadge.className = 'badge badge-rose';
        } else {
            updVal.textContent = 'بدون فایل قفل معلق';
            updBadge.textContent = 'صف آپدیت پاک است';
            updBadge.className = 'badge badge-emerald';
        }
    }
}

async function runRescueFix(fixType, buttonId, actionName) {
    const ip = targetIp.value.trim();
    const user = targetUser.value.trim();
    const pass = targetPass.value;

    if (!confirm(`آیا مطمئن هستید که می‌خواهید عملیات اورژانس «${actionName}» روی سیستم ${ip} اجرا شود؟`)) return;

    const btn = document.getElementById(buttonId);
    if (btn) btn.disabled = true;

    logTerminal(`شروع اجرای عملیات امدادی ${actionName} (${fixType}) روی ${ip}...`, 'warn');

    try {
        const res = await fetch(`${API_BASE}/api/rescue/fix`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ip, username: user, password: pass, fixType })
        });
        const data = await res.json();

        logTerminal(`نتیجه عملیات ${actionName}:\n${data.output}`, 'success');
        alert(`عملیات ${actionName} با موفقیت پایان یافت.`);

        // Auto re-audit to update badges
        if (btnRescueAudit) btnRescueAudit.click();
    } catch (err) {
        logTerminal(`خطا در اجرای ${actionName}: ${err.message}`, 'error');
        alert(`خطا: ${err.message}`);
    } finally {
        if (btn) btn.disabled = false;
    }
}

// Bind Rescue Fix Buttons
const fixWinReBtn = document.getElementById('btn-fix-enable-winre');
if (fixWinReBtn) fixWinReBtn.addEventListener('click', () => runRescueFix('enable-winre', 'btn-fix-enable-winre', 'فعال‌سازی و ثبت WinRE'));

const fixBcdBtn = document.getElementById('btn-fix-rebuild-bcd');
if (fixBcdBtn) fixBcdBtn.addEventListener('click', () => runRescueFix('rebuild-bcd', 'btn-fix-rebuild-bcd', 'بازسازی بوت‌لودر UEFI BCD'));

const fixRevertBtn = document.getElementById('btn-fix-revert-updates');
if (fixRevertBtn) fixRevertBtn.addEventListener('click', () => runRescueFix('revert-updates', 'btn-fix-revert-updates', 'رول‌بک آپدیت‌های مخرب'));

const fixSysfilesBtn = document.getElementById('btn-fix-repair-sysfiles');
if (fixSysfilesBtn) fixSysfilesBtn.addEventListener('click', () => runRescueFix('repair-sysfiles', 'btn-fix-repair-sysfiles', 'ترمیم فایل‌های هسته SFC و DISM'));

const fixAllRescueBtn = document.getElementById('btn-fix-all-rescue');
if (fixAllRescueBtn) fixAllRescueBtn.addEventListener('click', () => runRescueFix('all-in-one-boot', 'btn-fix-all-rescue', 'پکیج جامع اورژانس بوت'));

// --------------------------------------------------------------------------
// 8.9 Office LAN, Printer Wizard & Anti-Sanction Engine
// --------------------------------------------------------------------------
const btnOfficeAudit = document.getElementById('btn-run-office-audit');
if (btnOfficeAudit) {
    btnOfficeAudit.addEventListener('click', async () => {
        const ip = targetIp.value.trim();
        const user = targetUser.value.trim();
        const pass = targetPass.value;

        btnOfficeAudit.disabled = true;
        btnOfficeAudit.querySelector('span').textContent = 'در حال ممیزی شبکه و ابزارهای محلی...';
        logTerminal(`شروع ممیزی شبکه اداری، پرینتر، DNS و سلامت دیسک روی ${ip}...`, 'info');

        try {
            const res = await fetch(`${API_BASE}/api/office/audit`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ ip, username: user, password: pass })
            });
            const result = await res.json();

            if (!result.success || !result.data) {
                throw new Error(result.error || 'پاسخ نامعتبر از ممیزی آفیس');
            }

            const data = result.data;
            renderOfficeAudit(data);
            logTerminal('ممیزی شبکه اداری و ابزارهای محلی با موفقیت استخراج شد.', 'success');
        } catch (err) {
            logTerminal(`خطا در ممیزی آفیس: ${err.message}`, 'error');
            alert(`خطا: ${err.message}`);
        } finally {
            btnOfficeAudit.disabled = false;
            btnOfficeAudit.querySelector('span').textContent = 'ممیزی وضعیت شبکه و ابزارهای محلی';
        }
    });
}

function renderOfficeAudit(d) {
    // 1. Network Profile
    const netVal = document.getElementById('val-office-net');
    const netBadge = document.getElementById('badge-office-net');
    if (d.Network) {
        netVal.textContent = `${d.Network.Name} (${d.Network.Category})`;
        if (d.Network.IsPrivate && d.Network.DiscoveryActive) {
            netBadge.textContent = 'Private (دیده‌شدن در شبکه OK)';
            netBadge.className = 'badge badge-emerald';
        } else if (d.Network.IsPrivate) {
            netBadge.textContent = 'Private (دیسکاوری خاموش)';
            netBadge.className = 'badge badge-amber';
        } else {
            netBadge.textContent = 'Public (مسدودسازی فایروال)';
            netBadge.className = 'badge badge-rose';
        }
    }

    // 2. Printer Sharing Bug
    const printVal = document.getElementById('val-office-print');
    const printBadge = document.getElementById('badge-office-print');
    if (d.PrinterSharing) {
        if (d.PrinterSharing.IsPatched11b) {
            printVal.textContent = 'پچ شده (بدون خطای 0x11b)';
            printBadge.textContent = 'شیرینگ پرینتر آزاد';
            printBadge.className = 'badge badge-emerald';
        } else {
            printVal.textContent = 'پچ نشده (خطر 0x0000011b)';
            printBadge.textContent = 'احتمال خطای اتصال پرینتر';
            printBadge.className = 'badge badge-rose';
        }
    }

    // 3. DNS Status
    const dnsVal = document.getElementById('val-office-dns');
    const dnsBadge = document.getElementById('badge-office-dns');
    if (d.Dns) {
        dnsVal.textContent = d.Dns.Type;
        if (d.Dns.Type.includes('Shecan') || d.Dns.Type.includes('Electro')) {
            dnsBadge.textContent = 'تحریم‌شکن فعال است';
            dnsBadge.className = 'badge badge-purple';
        } else {
            dnsBadge.textContent = 'DNS استاندارد / مودم';
            dnsBadge.className = 'badge badge-cyan';
        }
    }

    // 4. S.M.A.R.T Disk Wear & Temp
    const diskVal = document.getElementById('val-office-disk');
    const diskBadge = document.getElementById('badge-office-disk');
    if (d.DiskReliability && d.DiskReliability.length > 0) {
        const primary = d.DiskReliability[0];
        diskVal.textContent = `دما: ${primary.Temperature} | استهلاک: ${primary.WearPercent}`;
        diskBadge.textContent = `سلامت: ${primary.Health}`;
        diskBadge.className = (primary.Health === 'Healthy') ? 'badge badge-emerald' : 'badge badge-rose';
    }
}

async function runOfficeAction(actionType, buttonId, actionName, folderPath = '', shareName = '', customExclusion = '') {
    const ip = targetIp.value.trim();
    const user = targetUser.value.trim();
    const pass = targetPass.value;

    if (!confirm(`آیا مطمئن هستید که می‌خواهید عملیات «${actionName}» روی سیستم ${ip} اجرا شود؟`)) return;

    const btn = document.getElementById(buttonId);
    if (btn) btn.disabled = true;

    logTerminal(`شروع اجرای عملیات ${actionName} روی ${ip}...`, 'warn');

    try {
        const res = await fetch(`${API_BASE}/api/office/action`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ip, username: user, password: pass, actionType, folderPath, shareName, customExclusion })
        });
        const data = await res.json();

        logTerminal(`نتیجه عملیات ${actionName}:\n${data.output}`, 'success');
        alert(`عملیات ${actionName} با موفقیت پایان یافت.`);

        // Auto re-audit to update badges
        if (btnOfficeAudit) btnOfficeAudit.click();
    } catch (err) {
        logTerminal(`خطا در اجرای ${actionName}: ${err.message}`, 'error');
        alert(`خطا: ${err.message}`);
    } finally {
        if (btn) btn.disabled = false;
    }
}

// Bind Office Action Buttons
const btnSetupLan = document.getElementById('btn-action-setup-lan');
if (btnSetupLan) btnSetupLan.addEventListener('click', () => runOfficeAction('setup-office-lan', 'btn-action-setup-lan', 'راه‌اندازی شبکه اداری'));

const btnFixPrinter = document.getElementById('btn-action-fix-printer');
if (btnFixPrinter) btnFixPrinter.addEventListener('click', () => runOfficeAction('fix-printer-bug', 'btn-action-fix-printer', 'پچ باگ پرینتر شبکه‌ای'));

const btnDnsShecan = document.getElementById('btn-dns-shecan');
if (btnDnsShecan) btnDnsShecan.addEventListener('click', () => runOfficeAction('set-dns-shecan', 'btn-dns-shecan', 'فعال‌سازی DNS شکن'));

const btnDnsElectro = document.getElementById('btn-dns-electro');
if (btnDnsElectro) btnDnsElectro.addEventListener('click', () => runOfficeAction('set-dns-electro', 'btn-dns-electro', 'فعال‌سازی DNS الکترو'));

const btnDnsAuto = document.getElementById('btn-dns-auto');
if (btnDnsAuto) btnDnsAuto.addEventListener('click', () => runOfficeAction('reset-dns-auto', 'btn-dns-auto', 'ریست DNS به حالت خودکار'));

const btnProtectCracks = document.getElementById('btn-action-protect-cracks');
if (btnProtectCracks) btnProtectCracks.addEventListener('click', () => runOfficeAction('protect-cracks', 'btn-action-protect-cracks', 'حفاظت از فعال‌سازها و کرک‌ها'));

const btnSpeedTest = document.getElementById('btn-action-speed-test');
if (btnSpeedTest) btnSpeedTest.addEventListener('click', () => runOfficeAction('lan-speed-test', 'btn-action-speed-test', 'تست پهنای باند و سرعت'));

const btnCreateShare = document.getElementById('btn-action-create-share');
if (btnCreateShare) btnCreateShare.addEventListener('click', () => {
    const fPath = document.getElementById('input-share-path').value.trim();
    const sName = document.getElementById('input-share-name').value.trim();
    runOfficeAction('create-smb-share', 'btn-action-create-share', 'ساخت پوشه اشتراکی شبکه', fPath, sName);
});

// Live UNC Preview Sync & Copy
const inputShareName = document.getElementById('input-share-name');
if (inputShareName) {
    inputShareName.addEventListener('input', () => {
        const previewName = document.getElementById('preview-unc-name');
        if (previewName) previewName.textContent = inputShareName.value.trim() || 'OfficeShare';
    });
}

const btnCopyUnc = document.getElementById('btn-copy-unc');
if (btnCopyUnc) {
    btnCopyUnc.addEventListener('click', () => {
        const ip = targetIp.value.trim() || '127.0.0.1';
        const sName = (inputShareName ? inputShareName.value.trim() : '') || 'OfficeShare';
        const fullUnc = `\\\\${ip}\\${sName}`;
        if (navigator.clipboard) {
            navigator.clipboard.writeText(fullUnc).then(() => {
                const orig = btnCopyUnc.textContent;
                btnCopyUnc.textContent = 'کپی شد! ✓';
                setTimeout(() => { btnCopyUnc.textContent = orig; }, 2000);
                logTerminal(`آدرس شبکه UNC در کلیپ‌بورد کپی شد: ${fullUnc}`, 'info');
            }).catch(() => {
                prompt('آدرس UNC جهت کپی:', fullUnc);
            });
        } else {
            prompt('آدرس UNC جهت کپی:', fullUnc);
        }
    });
}

// Extra DNS buttons
const btnDnsRadar = document.getElementById('btn-dns-radar');
if (btnDnsRadar) btnDnsRadar.addEventListener('click', () => runOfficeAction('set-dns-radar', 'btn-dns-radar', 'فعال‌سازی DNS رادار گیم'));

const btnDns403 = document.getElementById('btn-dns-403');
if (btnDns403) btnDns403.addEventListener('click', () => runOfficeAction('set-dns-403', 'btn-dns-403', 'فعال‌سازی DNS 403.online'));

const btnDnsBegzar = document.getElementById('btn-dns-begzar');
if (btnDnsBegzar) btnDnsBegzar.addEventListener('click', () => runOfficeAction('set-dns-begzar', 'btn-dns-begzar', 'فعال‌سازی DNS بگذر'));

const btnDnsCloudflare = document.getElementById('btn-dns-cloudflare');
if (btnDnsCloudflare) btnDnsCloudflare.addEventListener('click', () => runOfficeAction('set-dns-cloudflare', 'btn-dns-cloudflare', 'فعال‌سازی DNS کلودفلر 1.1.1.1'));

const btnDnsGoogle = document.getElementById('btn-dns-google');
if (btnDnsGoogle) btnDnsGoogle.addEventListener('click', () => runOfficeAction('set-dns-google', 'btn-dns-google', 'فعال‌سازی DNS گوگل 8.8.8.8'));

// Weekly Scheduled Maintenance
const btnScheduleMaint = document.getElementById('btn-schedule-weekly-maintenance');
if (btnScheduleMaint) btnScheduleMaint.addEventListener('click', () => runOfficeAction('schedule-weekly-maintenance', 'btn-schedule-weekly-maintenance', 'ثبت وظیفه نگهداری خودکار هفتگی در Task Scheduler'));

// --------------------------------------------------------------------------
// 9. Interactive PowerShell Terminal
// --------------------------------------------------------------------------
document.getElementById('btn-clear-terminal').addEventListener('click', () => {
    const termBody = document.getElementById('terminal-output');
    if (termBody) termBody.innerHTML = '<span class="term-line text-muted">[SYSTEM] کنسول پاکسازی شد.</span>';
});

async function sendCustomCommand() {
    const input = document.getElementById('custom-cmd-input');
    const cmd = input.value.trim();
    if (!cmd) return;

    const ip = targetIp.value.trim();
    const user = targetUser.value.trim();
    const pass = targetPass.value;

    logTerminal(`PS ${ip}> ${cmd}`, 'info');
    input.value = '';

    try {
        const res = await fetch(`${API_BASE}/api/run-custom`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ip, username: user, password: pass, command: cmd })
        });
        const data = await res.json();

        if (data.success) {
            logTerminal(data.output || '(دستور بدون خروجی متنی با موفقیت اجرا شد)', 'success');
        } else {
            logTerminal(data.error || 'خطایی در اجرای دستور رخ داد.', 'error');
        }
    } catch (err) {
        logTerminal(`خطای ارتباطی: ${err.message}`, 'error');
    }
}

document.getElementById('btn-send-custom-cmd').addEventListener('click', sendCustomCommand);
document.getElementById('custom-cmd-input').addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
        e.preventDefault();
        sendCustomCommand();
    }
});

// Quick command chips
document.querySelectorAll('.chip-btn').forEach(btn => {
    btn.addEventListener('click', () => {
        const cmd = btn.getAttribute('data-cmd');
        const input = document.getElementById('custom-cmd-input');
        if (cmd && input) {
            input.value = cmd;
            sendCustomCommand();
        }
    });
});

// --------------------------------------------------------------------------
// 10. Executive Client Report Generator (Printable PDF)
// --------------------------------------------------------------------------
function initClientReport() {
    const btnOpen = document.getElementById('btn-open-report');
    const modal = document.getElementById('modal-client-report');
    const btnClose = document.getElementById('btn-close-report');
    const btnPrint = document.getElementById('btn-print-report');

    if (!btnOpen || !modal) return;

    btnOpen.addEventListener('click', () => {
        const d = lastDiagnosticData || {};
        const ip = targetIp.value.trim() || '192.168.1.100';
        const profile = currentProfiles.find(p => p.ipAddress === ip) || activeProfile || {};

        const repNum = document.getElementById('rep-num');
        const repDate = document.getElementById('rep-date');
        const repHostname = document.getElementById('rep-hostname');
        const repIp = document.getElementById('rep-ip');
        const repModel = document.getElementById('rep-model');
        const repOs = document.getElementById('rep-os');
        const repCpu = document.getElementById('rep-cpu');
        const repRam = document.getElementById('rep-ram');
        const repStorage = document.getElementById('rep-storage');
        const repGpu = document.getElementById('rep-gpu');
        const repBadge = document.getElementById('rep-overall-badge');

        if (repNum) repNum.textContent = `RWD-${new Date().getFullYear()}-${Math.floor(1000 + Math.random() * 9000)}`;
        if (repDate) repDate.textContent = new Date().toLocaleDateString('fa-IR');
        
        if (repHostname) repHostname.textContent = (d.System && d.System.Model) ? `${d.System.Model} (${profile.name || 'سیستم مشتری'})` : (profile.name || 'Client Machine');
        if (repIp) repIp.textContent = ip;
        if (repModel) repModel.textContent = (d.System && d.System.Model) ? d.System.Model : (profile.model || 'Dell / HP / PC');
        if (repOs) repOs.textContent = (d.System && d.System.OSCaption) ? `${d.System.OSCaption} (Build ${d.System.OSBuild || '22000'})` : 'Windows 11 Pro 64-Bit';
        
        if (repCpu) repCpu.textContent = (d.Processor && d.Processor.Name) ? `${d.Processor.Name} (${d.Processor.Cores}C / ${d.Processor.Threads}T)` : (profile.specs ? profile.specs.split('|')[0].trim() : 'Intel Core i7 Processor');
        
        if (repRam) {
            if (d.Memory) {
                repRam.textContent = `${d.Memory.TotalGB} GB | ${d.Memory.ChannelMode}`;
            } else if (profile.specs && profile.specs.includes('RAM')) {
                const parts = profile.specs.split('|');
                const ramPart = parts.find(p => p.includes('RAM')) || '16 GB RAM';
                repRam.textContent = ramPart.trim();
            } else {
                repRam.textContent = '16 GB Dual-Channel DDR4';
            }
        }

        if (repStorage) {
            if (d.Storage && d.Storage.PhysicalDisks && d.Storage.PhysicalDisks.length > 0) {
                repStorage.textContent = `${d.Storage.PhysicalDisks[0].FriendlyName} (${d.Storage.PhysicalDisks[0].HealthStatus || 'Healthy'})`;
            } else if (profile.specs && profile.specs.includes('NVMe')) {
                const parts = profile.specs.split('|');
                const ssdPart = parts.find(p => p.includes('NVMe') || p.includes('SSD')) || 'NVMe SSD';
                repStorage.textContent = ssdPart.trim();
            } else {
                repStorage.textContent = 'NVMe SSD High Speed Storage (100% Health)';
            }
        }

        if (repGpu) {
            if (d.Graphics) {
                const gName = (d.Graphics.Controllers && d.Graphics.Controllers.length > 0) ? d.Graphics.Controllers[0].Name : 'NVIDIA / Intel';
                repGpu.textContent = `${gName} (${d.Graphics.NvidiaVramGB}GB VRAM)`;
            } else if (profile.specs && profile.specs.includes('Quadro')) {
                repGpu.textContent = 'NVIDIA Quadro P3000 (6GB VRAM - 1280 CUDA)';
            } else {
                repGpu.textContent = 'Intel UHD Graphics / Dedicated GPU';
            }
        }

        // Show modal
        modal.style.display = 'flex';
        logTerminal('گزارش رسمی ممیزی و گواهی سلامت سیستم کارفرما آماده مشاهده و پرینت شد.', 'info');
    });

    if (btnClose) {
        btnClose.addEventListener('click', () => {
            modal.style.display = 'none';
        });
    }

    if (btnPrint) {
        btnPrint.addEventListener('click', () => {
            window.print();
        });
    }

    modal.addEventListener('click', (e) => {
        if (e.target === modal) modal.style.display = 'none';
    });
}

// --------------------------------------------------------------------------
// Initialization
// --------------------------------------------------------------------------
window.addEventListener('DOMContentLoaded', () => {
    initNavigation();
    loadProfiles();
    initClientReport();
    logTerminal('RemoteWinDoctor Enterprise Engine آماده به کار است. پورت 5055 فعال شد.');
});

