using System.Net.Sockets;

namespace AgentFactory.App.Fleet;

/// <summary>
/// Port takeover for the fixed opencode server port (ticket 023): a hard-killed
/// host (task-manager kill, session-runner stop) never runs
/// <c>DisposeAsync</c>, so the <c>opencode serve</c> child is orphaned and keeps
/// holding the port — the next boot's fresh serve gets the stale listener's 401s
/// and the health gate spins out. Before spawning, the engine probes the port;
/// a listener is killed only when its command line is an
/// <c>opencode … serve</c> process. Any other holder (or none resolvable) fails
/// loudly instead of killing an arbitrary process.
/// </summary>
public static partial class PortOwnership
{
    /// <summary>True when something accepts TCP connections on the port.</summary>
    public static async Task<bool> IsHeldAsync(int port, CancellationToken ct = default)
    {
        try
        {
            using var probe = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
            await probe.ConnectAsync("127.0.0.1", port, timeout.Token);
            return true; // connected → a listener answered
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false; // connection refused/canceled → nothing there
        }
    }

    /// <summary>Free the port for a fresh serve. No listener is a no-op; an
    /// orphaned <c>opencode serve</c> is killed; anything else throws. A port
    /// that answers connects but resolves no LISTENING owner is the normal
    /// teardown transition right after a tree-kill (ticket 042's restart race)
    /// — waited out, not failed: only a steady ownerless hold is loud.</summary>
    public static async Task ReclaimAsync(int port, ILogger logger, CancellationToken ct = default)
    {
        List<int> pids;
        for (var attempt = 0; ; attempt++)
        {
            if (!await IsHeldAsync(port, ct)) return;
            pids = ListenerPids(port);
            if (pids.Count > 0) break;
            if (attempt >= 10)
            {
                throw new InvalidOperationException(
                    $"port {port} is held but no listening PID could be resolved " +
                    $"(netstat/lsof found nothing, waited {(attempt + 1) * 500} ms) — free it manually and restart");
            }
            await Task.Delay(500, ct);
        }

        foreach (var pid in pids)
        {
            var commandLine = CommandLineOf(pid);
            if (commandLine is null
                || !commandLine.Contains("opencode", StringComparison.OrdinalIgnoreCase)
                || !commandLine.Contains("serve", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"port {port} is held by pid {pid}, which is not an opencode serve process " +
                    $"(command line: \"{commandLine ?? "<unavailable>"}\") — refusing to kill it; " +
                    $"free the port manually and restart");
            }

            logger.LogWarning(
                "killing orphaned opencode serve (pid {Pid}) holding port {Port}: {Command}",
                pid, port, commandLine);
            Kill(pid);
        }

        // The killed listener may take a moment to release the socket.
        for (var attempt = 0; attempt < 20 && await IsHeldAsync(port, ct); attempt++)
        {
            await Task.Delay(500, ct);
        }
        if (await IsHeldAsync(port, ct))
        {
            throw new InvalidOperationException(
                $"port {port} is still held after killing {string.Join(", ", pids)} — free it manually and restart");
        }
        logger.LogInformation("port {Port} reclaimed for a fresh opencode serve", port);
    }

    /// <summary>PIDs with a LISTENING socket on the port (Windows netstat,
    /// Unix lsof; both are present wherever the app runs).</summary>
    private static List<int> ListenerPids(int port)
    {
        var pids = new List<int>();
        if (OperatingSystem.IsWindows())
        {
            var result = Proc.RunAsync("netstat", $"-ano -p tcp").Result;
            foreach (var line in result.Output.Split('\n'))
            {
                // TCP    127.0.0.1:48180    0.0.0.0:0    LISTENING    4242
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 5 && parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase)
                    && parts[1].EndsWith($":{port}"))
                {
                    pids.Add(int.Parse(parts[4]));
                }
            }
        }
        else
        {
            var result = Proc.RunAsync("lsof", $"-nP -iTCP:{port} -sTCP:LISTEN").Result;
            foreach (var line in result.Output.Split('\n').Skip(1))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2 && int.TryParse(parts[1], out var pid)) pids.Add(pid);
            }
        }
        return [.. pids.Distinct()];
    }

    /// <summary>Full command line of a pid, or null when it can't be read.</summary>
    private static string? CommandLineOf(int pid)
    {
        if (OperatingSystem.IsWindows())
        {
            // wmic is gone on current Windows; CIM via PowerShell is the reliable read.
            var ps = "-NoProfile -Command " +
                $"(Get-CimInstance Win32_Process -Filter \"ProcessId={pid}\").CommandLine";
            var result = Proc.RunAsync("powershell", ps).Result;
            return result.Ok ? result.Output.Trim() : null;
        }
        var psResult = Proc.RunAsync("ps", $"-p {pid} -o args=").Result;
        return psResult.Ok ? psResult.Output.Trim() : null;
    }

    private static void Kill(int pid)
    {
        if (OperatingSystem.IsWindows())
        {
            Proc.RunAsync("taskkill", $"/PID {pid} /T /F").Wait();
        }
        else
        {
            Proc.RunAsync("kill", $"{pid}").Wait();
        }
    }
}
