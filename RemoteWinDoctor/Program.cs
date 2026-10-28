using System.Diagnostics;
using System.Text.Json;
using RemoteWinDoctor.Services;

var builder = WebApplication.CreateBuilder(args);

// Register Services
builder.Services.AddSingleton<ProfileStore>();
builder.Services.AddSingleton<NetworkScanner>();
builder.Services.AddSingleton<PowerShellExecutor>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// 1. Profile Endpoints
app.MapGet("/api/profiles", (ProfileStore store) => Results.Ok(store.GetAll()));

app.MapPost("/api/profiles", (MachineProfile profile, ProfileStore store) =>
{
    if (string.IsNullOrWhiteSpace(profile.Id)) profile.Id = Guid.NewGuid().ToString("N");
    store.Save(profile);
    return Results.Ok(profile);
});

app.MapDelete("/api/profiles/{id}", (string id, ProfileStore store) =>
{
    var deleted = store.Delete(id);
    return deleted ? Results.Ok() : Results.NotFound();
});

// 2. Network Scanner Endpoints
app.MapPost("/api/scan", async (ScanRequest req, NetworkScanner scanner) =>
{
    var subnet = string.IsNullOrWhiteSpace(req.Subnet) ? "192.168.1" : req.Subnet.TrimEnd('.');
    var results = await scanner.ScanSubnetAsync(subnet, req.Start, req.End);
    return Results.Ok(results);
});

app.MapPost("/api/probe", async (ProbeRequest req, NetworkScanner scanner) =>
{
    if (string.IsNullOrWhiteSpace(req.Ip)) return Results.BadRequest("IP is required");
    var device = await scanner.ProbeHostAsync(req.Ip);
    return Results.Ok(device);
});

// 3. Diagnostic Engine
app.MapPost("/api/diagnose", async (ActionRequest req, PowerShellExecutor executor) =>
{
    var script = DiagnosticEngine.GetDeepDiagnosticScript();
    var result = await executor.ExecuteAsync(req.Ip, req.Username, req.Password, script, timeoutSeconds: 90);

    if (!result.Success && string.IsNullOrWhiteSpace(result.Output))
    {
        return Results.Json(new { success = false, error = result.Error, output = result.Output }, statusCode: 500);
    }

    try
    {
        // Try parsing JSON output
        var json = result.Output.Trim();
        var startIndex = json.IndexOf('{');
        var endIndex = json.LastIndexOf('}');
        if (startIndex >= 0 && endIndex > startIndex)
        {
            var extracted = json.Substring(startIndex, endIndex - startIndex + 1);
            using var doc = JsonDocument.Parse(extracted);
            return Results.Json(new { success = true, data = doc.RootElement.Clone(), raw = result.Output });
        }
    }
    catch
    {
        // Fallback to raw output
    }

    return Results.Json(new { success = result.Success, raw = result.Output, error = result.Error });
});

// 4. Optimization Engine
app.MapPost("/api/optimize", async (ActionRequest req, PowerShellExecutor executor) =>
{
    var script = DiagnosticEngine.GetOptimizationScript();
    var result = await executor.ExecuteAsync(req.Ip, req.Username, req.Password, script, timeoutSeconds: 180);
    return Results.Json(new
    {
        success = result.Success,
        exitCode = result.ExitCode,
        output = result.Output,
        error = result.Error,
        durationSeconds = Math.Round(result.Duration.TotalSeconds, 1)
    });
});

// 5. Driver Injector
app.MapPost("/api/install-drivers", async (DriverInstallRequest req, PowerShellExecutor executor) =>
{
    if (string.IsNullOrWhiteSpace(req.FolderPath))
    {
        return Results.BadRequest("Source folder path is required.");
    }
    var script = DiagnosticEngine.GetDriverInstallScript(req.FolderPath);
    var result = await executor.ExecuteAsync(req.Ip, req.Username, req.Password, script, timeoutSeconds: 300);
    return Results.Json(new
    {
        success = result.Success,
        output = result.Output,
        error = result.Error
    });
});

// 6. Remote Reboot
app.MapPost("/api/reboot", async (ActionRequest req, PowerShellExecutor executor) =>
{
    var script = "Restart-Computer -Force -Confirm:$false; Write-Host 'Reboot signal dispatched.'";
    var result = await executor.ExecuteAsync(req.Ip, req.Username, req.Password, script, timeoutSeconds: 15);
    return Results.Json(new { success = true, message = "Reboot command sent to remote machine." });
});

// 7. Custom PowerShell Command
app.MapPost("/api/run-custom", async (CustomCommandRequest req, PowerShellExecutor executor) =>
{
    if (string.IsNullOrWhiteSpace(req.Command)) return Results.BadRequest("Command cannot be empty.");
    var result = await executor.ExecuteAsync(req.Ip, req.Username, req.Password, req.Command, timeoutSeconds: 60);
    return Results.Json(new
    {
        success = result.Success,
        output = result.Output,
        error = result.Error,
        durationMs = result.Duration.TotalMilliseconds
    });
});

// 8. Database & Dev Toolkit Endpoints
app.MapPost("/api/devdb/audit", async (ActionRequest req, PowerShellExecutor executor) =>
{
    var script = DevDbFixer.GetAuditScript();
    var result = await executor.ExecuteAsync(req.Ip, req.Username, req.Password, script, timeoutSeconds: 60);

    try
    {
        var json = result.Output.Trim();
        var startIndex = json.IndexOf('{');
        var endIndex = json.LastIndexOf('}');
        if (startIndex >= 0 && endIndex > startIndex)
        {
            var extracted = json.Substring(startIndex, endIndex - startIndex + 1);
            using var doc = JsonDocument.Parse(extracted);
            return Results.Json(new { success = true, data = doc.RootElement.Clone(), raw = result.Output });
        }
    }
    catch { }

    return Results.Json(new { success = result.Success, raw = result.Output, error = result.Error });
});

app.MapPost("/api/devdb/fix", async (DevDbFixRequest req, PowerShellExecutor executor) =>
{
    var script = DevDbFixer.GetFixScript(req.FixType);
    var result = await executor.ExecuteAsync(req.Ip, req.Username, req.Password, script, timeoutSeconds: 180);
    return Results.Json(new
    {
        success = result.Success,
        output = result.Output,
        error = result.Error,
        durationSeconds = Math.Round(result.Duration.TotalSeconds, 1)
    });
});

// 9. Rescue & Boot Doctor Endpoints
app.MapPost("/api/rescue/audit", async (ActionRequest req, PowerShellExecutor executor) =>
{
    var script = RescueDoctor.GetBootAuditScript();
    var result = await executor.ExecuteAsync(req.Ip, req.Username, req.Password, script, timeoutSeconds: 60);

    try
    {
        var json = result.Output.Trim();
        var startIndex = json.IndexOf('{');
        var endIndex = json.LastIndexOf('}');
        if (startIndex >= 0 && endIndex > startIndex)
        {
            var extracted = json.Substring(startIndex, endIndex - startIndex + 1);
            using var doc = JsonDocument.Parse(extracted);
            return Results.Json(new { success = true, data = doc.RootElement.Clone(), raw = result.Output });
        }
    }
    catch { }

    return Results.Json(new { success = result.Success, raw = result.Output, error = result.Error });
});

app.MapPost("/api/rescue/fix", async (RescueFixRequest req, PowerShellExecutor executor) =>
{
    var script = RescueDoctor.GetRescueFixScript(req.FixType);
    var result = await executor.ExecuteAsync(req.Ip, req.Username, req.Password, script, timeoutSeconds: 180);
    return Results.Json(new
    {
        success = result.Success,
        output = result.Output,
        error = result.Error,
        durationSeconds = Math.Round(result.Duration.TotalSeconds, 1)
    });
});

app.MapGet("/api/rescue/download-script", () =>
{
    var content = RescueDoctor.GetOfflineRescueScriptContent();
    var bytes = System.Text.Encoding.UTF8.GetBytes(content);
    return Results.File(bytes, "application/octet-stream", "RescueBootDoctor.bat");
});

// 10. Local Office, Printer & Anti-Sanction Toolkit Endpoints
app.MapPost("/api/office/audit", async (ActionRequest req, PowerShellExecutor executor) =>
{
    var script = LocalOfficeToolkit.GetLanAuditScript();
    var result = await executor.ExecuteAsync(req.Ip, req.Username, req.Password, script, timeoutSeconds: 60);

    try
    {
        var json = result.Output.Trim();
        var startIndex = json.IndexOf('{');
        var endIndex = json.LastIndexOf('}');
        if (startIndex >= 0 && endIndex > startIndex)
        {
            var extracted = json.Substring(startIndex, endIndex - startIndex + 1);
            using var doc = JsonDocument.Parse(extracted);
            return Results.Json(new { success = true, data = doc.RootElement.Clone(), raw = result.Output });
        }
    }
    catch { }

    return Results.Json(new { success = result.Success, raw = result.Output, error = result.Error });
});

app.MapPost("/api/office/action", async (LocalOfficeActionRequest req, PowerShellExecutor executor) =>
{
    var script = LocalOfficeToolkit.GetLanActionScript(req.ActionType, req.FolderPath, req.ShareName, req.CustomExclusion);
    var result = await executor.ExecuteAsync(req.Ip, req.Username, req.Password, script, timeoutSeconds: 120);
    return Results.Json(new
    {
        success = result.Success,
        output = result.Output,
        error = result.Error,
        durationSeconds = Math.Round(result.Duration.TotalSeconds, 1)
    });
});

// Auto-launch browser on startup
var port = 5055;

app.Urls.Add($"http://localhost:{port}");

_ = Task.Run(async () =>
{
    await Task.Delay(1200);
    try
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = $"http://localhost:{port}",
            UseShellExecute = true
        });
    }
    catch { }
});

Console.WriteLine($"=================================================");
Console.WriteLine($"  RemoteWinDoctor - Enterprise PC Optimizer & AI Lab");
Console.WriteLine($"  Live Dashboard running on: http://localhost:{port}");
Console.WriteLine($"=================================================");

app.Run();

// Request DTOs
public record ScanRequest(string Subnet = "192.168.1", int Start = 1, int End = 254);
public record ProbeRequest(string Ip);
public record ActionRequest(string Ip, string Username, string Password);
public record DriverInstallRequest(string Ip, string Username, string Password, string FolderPath);
public record CustomCommandRequest(string Ip, string Username, string Password, string Command);
