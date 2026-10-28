using System.Diagnostics;
using System.Text;

namespace RemoteWinDoctor.Services;

public class ExecutionResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
}

/// <summary>A long-running PowerShell process whose stdout lines are delivered as they arrive.</summary>
public sealed class PowerShellStream : IDisposable
{
    private readonly Process _process;
    private readonly string _tempScript;
    private int _disposed;

    internal PowerShellStream(Process process, string tempScript)
    {
        _process = process;
        _tempScript = tempScript;
    }

    public bool HasExited
    {
        get { try { return _process.HasExited; } catch (InvalidOperationException) { return true; } }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(true);
                _process.WaitForExit(3000); // the script file stays locked until powershell.exe is gone
            }
        }
        catch { }
        _process.Dispose();
        try { if (File.Exists(_tempScript)) File.Delete(_tempScript); } catch { }
    }
}

public class PowerShellExecutor
{
    private const string EmptyPasswordError = "No password entered for the remote PC.";

    private static readonly HashSet<string> LocalAddresses = GetLocalAddresses();

    private static HashSet<string> GetLocalAddresses()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "127.0.0.1", "localhost", "::1", Environment.MachineName };
        try
        {
            foreach (var a in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
                set.Add(a.ToString());
        }
        catch { }
        return set;
    }

    /// <summary>True for loopback or any of this machine's own addresses/name; such targets run in-process, without WinRM.</summary>
    public static bool IsLocalTarget(string? ip) => string.IsNullOrWhiteSpace(ip) || LocalAddresses.Contains(ip.Trim());

    private static string BuildScript(string ip, string scriptBlock, bool isLocal)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Console]::OutputEncoding = [System.Text.Encoding]::UTF8");
        sb.AppendLine("$OutputEncoding = [System.Text.Encoding]::UTF8");
        if (isLocal)
        {
            sb.AppendLine(scriptBlock);
        }
        else
        {
            // Credentials travel via the child process's environment block, never inside
            // the temp .ps1, so a crash or hang can't leave the password on disk.
            sb.AppendLine("$sec = ConvertTo-SecureString $env:RWD_PASS -AsPlainText -Force");
            sb.AppendLine("$cred = New-Object System.Management.Automation.PSCredential($env:RWD_USER, $sec)");
            sb.AppendLine("Remove-Item Env:RWD_PASS, Env:RWD_USER -ErrorAction SilentlyContinue");
            sb.AppendLine($"Invoke-Command -ComputerName '{ip}' -Credential $cred -ScriptBlock {{");
            sb.AppendLine("    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8");
            sb.AppendLine("    $OutputEncoding = [System.Text.Encoding]::UTF8");
            sb.AppendLine(scriptBlock);
            sb.AppendLine("}");
        }
        return sb.ToString();
    }

    private static async Task<(ProcessStartInfo Psi, string TempScript)> PrepareAsync(string ip, string username, string password, string scriptBlock, bool isLocal)
    {
        var tempScript = Path.Combine(Path.GetTempPath(), $"RemoteWinDoctor_{Guid.NewGuid():N}.ps1");
        // UTF-8 BOM so Windows PowerShell 5.1 parses Unicode/Persian characters
        await File.WriteAllTextAsync(tempScript, BuildScript(ip, scriptBlock, isLocal), new UTF8Encoding(true));

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tempScript}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (!isLocal)
        {
            psi.Environment["RWD_USER"] = username;
            psi.Environment["RWD_PASS"] = password;
        }
        return (psi, tempScript);
    }

    public async Task<ExecutionResult> ExecuteAsync(string ip, string username, string password, string scriptBlock, int timeoutSeconds = 120, Action<string>? liveOutputCallback = null)
    {
        var sw = Stopwatch.StartNew();
        bool isLocal = IsLocalTarget(ip);

        // An empty remote password makes WinRM fall back to Windows' native credential
        // dialog, which can hide behind the app and hang it; fail fast instead.
        if (!isLocal && string.IsNullOrEmpty(password))
        {
            return new ExecutionResult { Success = false, ExitCode = -1, Error = EmptyPasswordError, Duration = sw.Elapsed };
        }

        var (psi, tempScript) = await PrepareAsync(ip, username, password, scriptBlock, isLocal);
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        using var process = new Process { StartInfo = psi };

        process.OutputDataReceived += (s, e) =>
        {
            if (e.Data != null)
            {
                outputBuilder.AppendLine(e.Data);
                liveOutputCallback?.Invoke(e.Data);
            }
        };

        process.ErrorDataReceived += (s, e) =>
        {
            if (e.Data != null)
            {
                errorBuilder.AppendLine(e.Data);
                liveOutputCallback?.Invoke("[ERR] " + e.Data);
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var waitTask = process.WaitForExitAsync();
            var delayTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));

            if (await Task.WhenAny(waitTask, delayTask) == delayTask)
            {
                try { process.Kill(true); } catch { }
                return new ExecutionResult
                {
                    Success = false,
                    ExitCode = -1,
                    Output = outputBuilder.ToString(),
                    Error = $"Execution timed out after {timeoutSeconds} seconds.",
                    Duration = sw.Elapsed
                };
            }

            sw.Stop();
            return new ExecutionResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = outputBuilder.ToString(),
                Error = errorBuilder.ToString(),
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ExecutionResult
            {
                Success = false,
                ExitCode = -1,
                Output = outputBuilder.ToString(),
                Error = ex.Message,
                Duration = sw.Elapsed
            };
        }
        finally
        {
            try { if (File.Exists(tempScript)) File.Delete(tempScript); } catch { }
        }
    }

    /// <summary>
    /// Starts a script that keeps running (e.g. a sampling loop) and delivers each stdout line
    /// as it arrives. For remote targets this is a single Invoke-Command, so one WinRM session
    /// serves the whole stream instead of reconnecting per sample. Dispose to stop it.
    /// Callbacks run on thread-pool threads.
    /// </summary>
    public async Task<PowerShellStream> StartStreamAsync(string ip, string username, string password, string scriptBlock,
        Action<string> onLine, Action<string> onError, Action onExit)
    {
        bool isLocal = IsLocalTarget(ip);
        if (!isLocal && string.IsNullOrEmpty(password))
            throw new InvalidOperationException(EmptyPasswordError);

        var (psi, tempScript) = await PrepareAsync(ip, username, password, scriptBlock, isLocal);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) onLine(e.Data); };
        process.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) onError(e.Data); };
        process.Exited += (s, e) => onExit();

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new PowerShellStream(process, tempScript);
    }
}
