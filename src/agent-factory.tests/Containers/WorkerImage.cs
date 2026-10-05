namespace AgentFactory.Tests.Containers;

using AgentFactory.Tests.Boundary;

using System.Diagnostics;

/// <summary>
/// The image a worker container starts from, and the daemon that has to be able to start
/// it. Both are the development machine's business rather than the factory's, so a test
/// that needs them skips and says why instead of failing a machine that has neither.
/// </summary>
public static class WorkerImage
{
    /// <summary>
    /// The image a project file names, and the one the examples and <c>worker/README.md</c>
    /// all say. Built from <c>worker/Dockerfile</c>.
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

    /// <summary>
    /// Whether the OpenCode CLI inside the image can actually be driven to a provider on
    /// this machine, or why it cannot. This is a separate probe from the daemon and the
    /// image because it answers a different question: a worker container can start
    /// perfectly well and still have no way to reach a model, which is the one thing an
    /// agent round cannot be run without.
    /// </summary>
    /// <remarks>
    /// It is a probe and not an assertion, and it is deliberately cheap: one short prompt,
    /// no repository, no filesystem, a bounded wait. What it establishes is only whether a
    /// credential reaches a provider from inside the image — nothing about the quality of
    /// what comes back, which is not a thing a test can or should measure.
    /// </remarks>
    public static string? AgentUnavailableBecause => AgentUnavailable.Value;

    /// <summary>Whether a real agent round can be run on this machine right now.</summary>
    public static bool AgentAvailable => UnavailableBecause is null && AgentUnavailableBecause is null;

    private static readonly Lazy<string?> AgentUnavailable = new(ProbeTheAgent, isThreadSafe: true);

    private static string? ProbeTheAgent()
    {
        if (UnavailableBecause is { } unreachable)
        {
            return unreachable;
        }

        // A credential is looked for in the environment under the canonical name the round
        // actually injects (#38) — the name the provider's tooling reads. Its *value* is
        // never read here or logged: this asks whether the CLI can reach the provider, not
        // what the provider is called.
        const string keyName = "ANTHROPIC_API_KEY";
        var configured = Environment.GetEnvironmentVariable(keyName);

        // Through the image's own entrypoint rather than around it, so the probe is asked
        // the same question a round asks: can the CLI, as uid 1000, in this image, reach
        // the provider the project's model names and come back with an answer. The model
        // is the project's, and it is stated — a probe without one answers for whichever
        // credential-free default the CLI ships with, which is how a machine with no key
        // at all once passed this probe and failed the round it was probing for.
        var pass = configured is { Length: > 0 }
            ? new[] { "run", "--rm", "-e", keyName, Tag }
            : new[] { "run", "--rm", Tag };

        var answered = Docker(
            pass
                .Append("exec")
                .Append("bash")
                .Append("-c")
                .Append($"opencode run --standalone --log-level none -m {ProjectFile.Model} "
                    + "'Reply with the single word OK and nothing else.' 2>&1")
                .ToArray());

        if (answered is null || !answered.Contains("OK", StringComparison.OrdinalIgnoreCase))
        {
            return configured is { Length: > 0 }
                ? $"the OpenCode CLI in {Tag} could not reach {ProjectFile.Model} even with {keyName} configured, "
                    + "so a round cannot be driven with the agent here. The test that needs an agent is about the "
                    + "factory's derivation, which is covered hermetically and by the container tests; this one is "
                    + "about the agent actually running"
                : $"no LLM credential is configured in this environment ({keyName} is not set) and the OpenCode CLI "
                    + $"in {Tag} cannot reach {ProjectFile.Model} without one, so a round cannot be driven with the "
                    + "agent here. The test that needs an agent is about the factory's derivation, which is covered "
                    + "hermetically and by the container tests; this one is about the agent actually running";
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

/// <summary>
/// A test that needs a real Docker daemon, the real worker image, and a provider the
/// agent inside it can actually reach. On a machine with no daemon or no image it skips
/// for the reason <see cref="DockerFactAttribute"/> gives; on a machine that has both but
/// no LLM credential it skips saying that, rather than failing a machine whose only
/// problem is that it has no API key.
/// </summary>
/// <remarks>
/// This is the seam the credential problem is honestly handled behind. An agent round
/// genuinely cannot run without one, and inventing a key or faking the provider would make
/// a green suite that proves nothing about the agent. What the factory does *not* need a
/// key for — deriving a result, recording commands and outcomes, degrading to a log — is
/// tested without one and without a container, and the deterministic container round in
/// <see cref="AgentRoundTests"/> covers the rest.
/// </remarks>
public sealed class AgentFactAttribute : FactAttribute
{
    public AgentFactAttribute()
    {
        if (WorkerImage.AgentUnavailableBecause is { } reason)
        {
            Skip = $"skipped: {reason}";
        }
    }
}
