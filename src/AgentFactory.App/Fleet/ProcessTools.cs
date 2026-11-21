using System.Diagnostics;
using System.Text;

namespace AgentFactory.App.Fleet;

/// <summary>
/// Resolves tool executables (git, gh, opencode) to absolute paths once, so
/// Process.Start with UseShellExecute=false works regardless of shim layout —
/// `where.exe` on Windows, a PATH scan (execute-bit checked) elsewhere.
/// </summary>
public static class ToolLocator
{
    private static readonly Dictionary<string, string> Cache = [];

    public static string Resolve(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
            var resolved = ResolveCore(name) ?? name;
            Cache[name] = resolved;
            return resolved;
        }
    }

    private static string? ResolveCore(string name)
    {
        if (File.Exists(name) || Path.IsPathRooted(name)) return Path.GetFullPath(name);

        if (OperatingSystem.IsWindows())
        {
            var where = Path.Combine(Environment.SystemDirectory, "where.exe");
            var hits = RunCapture(where, name);
            return hits.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(File.Exists);
        }

        // POSIX: the same scan `which` does, without spawning it — the app
        // runs where /usr/bin/env may be the only guaranteed neighbor, and a
        // bare name would still work via CreateProcess-style fallback if the
        // scan finds nothing.
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, name);
                if (!File.Exists(candidate)) continue;
                var mode = File.GetUnixFileMode(candidate);
                if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                    return Path.GetFullPath(candidate);
            }
            catch (IOException)
            {
                // unreadable PATH entry — skip it
            }
        }
        return null;
    }

    internal static (string StandardOutput, int ExitCode) RunCapture(string fileName, string args)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException($"failed to start {fileName}");
        process.WaitForExit(10_000);
        return (process.StandardOutput.ReadToEnd(), process.ExitCode);
    }
}

/// <summary>The outcome of one tool invocation.</summary>
public sealed record ProcResult(int ExitCode, string Output)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Runs git/gh commands, capturing stdout+stderr for the run log.</summary>
public static class Proc
{
    public static async Task<ProcResult> RunAsync(
        string tool, string args, string? workingDir = null, CancellationToken ct = default)
    {
        var exe = ToolLocator.Resolve(tool);
        using var process = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            WorkingDirectory = workingDir,
        }) ?? throw new InvalidOperationException($"failed to start {tool} {args}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ProcResult(process.ExitCode, stdout.Trim() + (stderr.Trim() is { Length: > 0 } e ? "\n" + e : ""));
    }

    /// <summary>Like <see cref="RunAsync"/> but writes <paramref name="stdin"/> to the process first
    /// (for gh's --body-file -).</summary>
    public static async Task<ProcResult> RunStdinAsync(
        string tool, string args, string stdin, CancellationToken ct = default)
    {
        var exe = ToolLocator.Resolve(tool);
        using var process = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException($"failed to start {tool} {args}");

        await process.StandardInput.WriteAsync(stdin.AsMemory(), ct);
        await process.StandardInput.FlushAsync(ct);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ProcResult(process.ExitCode, stdout.Trim() + (stderr.Trim() is { Length: > 0 } e ? "\n" + e : ""));
    }
}
