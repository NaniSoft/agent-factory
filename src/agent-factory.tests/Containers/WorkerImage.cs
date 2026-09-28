namespace AgentFactory.Tests.Containers;

using System.Diagnostics;

/// <summary>
/// The image a worker container starts from, and the daemon that has to be able to start
/// it. Both are the development machine's business rather than the factory's, so a test
/// that needs them skips and says why instead of failing a machine that has neither.
/// </summary>
public static class WorkerImage
{
    /// <summary>
    /// The image a project file names, and the one <c>factories/nexus.yaml</c> and
    /// <c>worker/README.md</c> both say. Built from <c>worker/Dockerfile</c>.
    /// </summary>
    public const string Tag = "ghcr.io/nanisoft/agent-factory-worker:1";

    private static readonly Lazy<string?> Unavailable = new(Probe, isThreadSafe: true);

    /// <summary>Why the worker image tests cannot run here, or null when they can.</summary>
    public static string? UnavailableBecause => Unavailable.Value;

    /// <summary>Whether a worker container can really be started on this machine right now.</summary>
    public static bool Available => UnavailableBecause is null;

    private static string? Probe()
    {
        if (Docker("version", "--format", "{{.Server.Version}}") is not { } version)
        {
            return "there is no Docker daemon on this machine, so no worker container can be started";
        }

        if (version is "unknown" or "")
        {
            return "the Docker CLI is installed but no daemon answered it";
        }

        if (Docker("image", "inspect", Tag) is null)
        {
            return $"the worker image {Tag} is not on this machine; build it with "
                + "`docker build -t ghcr.io/nanisoft/agent-factory-worker:1 worker/`";
        }

        return null;
    }

    /// <summary>Runs a docker command, returning its output, or null if it could not be run.</summary>
    public static string? Docker(params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(30_000);

            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// A test that needs a real Docker daemon and the real worker image. On a machine
/// without either it is skipped with the reason, so the suite is not broken by a
/// development environment rather than failing it.
/// </summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (WorkerImage.UnavailableBecause is { } reason)
        {
            Skip = $"skipped: {reason}";
        }
    }
}
