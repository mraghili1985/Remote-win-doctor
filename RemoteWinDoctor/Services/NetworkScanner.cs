using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteWinDoctor.Services;

public class DiscoveredDevice
{
    public string IpAddress { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public bool PingSucceeded { get; set; }
    public long RoundtripTimeMs { get; set; }
    public bool WinRmPortOpen { get; set; }
    public string StatusBadge => WinRmPortOpen ? "WinRM Active" : (PingSucceeded ? "Online" : "Offline");
}

public class NetworkScanner
{
    public async Task<List<DiscoveredDevice>> ScanSubnetAsync(string baseSubnet = "192.168.1", int startRange = 1, int endRange = 254, Action<int, int>? progressCallback = null)
    {
        var discovered = new List<DiscoveredDevice>();
        var total = endRange - startRange + 1;
        var completed = 0;

        using var semaphore = new SemaphoreSlim(40); // 40 concurrent scans for ultra-fast response

        var tasks = Enumerable.Range(startRange, total).Select(async i =>
        {
            var ip = $"{baseSubnet}.{i}";
            await semaphore.WaitAsync();
            try
            {
                var device = await ProbeHostAsync(ip);
                if (device.PingSucceeded || device.WinRmPortOpen)
                {
                    lock (discovered)
                    {
                        discovered.Add(device);
                    }
                }
            }
            catch
            {
                // Ignore timeout or unreachable
            }
            finally
            {
                semaphore.Release();
                var current = Interlocked.Increment(ref completed);
                progressCallback?.Invoke(current, total);
            }
        });

        await Task.WhenAll(tasks);

        return discovered.OrderBy(d =>
        {
            var parts = d.IpAddress.Split('.');
            return parts.Length == 4 && int.TryParse(parts[3], out var last) ? last : 999;
        }).ToList();
    }

    public async Task<DiscoveredDevice> ProbeHostAsync(string ip)
    {
        var device = new DiscoveredDevice { IpAddress = ip };

        // 1. Probe Port 5985 (WinRM)
        try
        {
            using var tcp = new TcpClient();
            var connectTask = tcp.ConnectAsync(ip, 5985);
            var completedTask = await Task.WhenAny(connectTask, Task.Delay(350));
            if (completedTask == connectTask && tcp.Connected)
            {
                device.WinRmPortOpen = true;
            }
        }
        catch
        {
            device.WinRmPortOpen = false;
        }

        // 2. Probe ICMP Ping
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, 300);
            if (reply.Status == IPStatus.Success)
            {
                device.PingSucceeded = true;
                device.RoundtripTimeMs = reply.RoundtripTime;
            }
        }
        catch
        {
            device.PingSucceeded = false;
        }

        // 3. Reverse DNS Hostname lookup
        if (device.PingSucceeded || device.WinRmPortOpen)
        {
            try
            {
                var hostEntryTask = Dns.GetHostEntryAsync(ip);
                var completed = await Task.WhenAny(hostEntryTask, Task.Delay(300));
                if (completed == hostEntryTask)
                {
                    device.Hostname = hostEntryTask.Result.HostName;
                }
            }
            catch
            {
                device.Hostname = string.Empty;
            }
        }

        return device;
    }
}
