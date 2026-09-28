namespace AgentFactory.Tests.Containers;

using AgentFactory.Containers;
using AgentFactory.Rounds;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// A round, in a real container, on a real daemon. These are the tests a fake cannot
/// write: that the image starts, that the command in it runs, that the log really streams
/// out while the round is going, that <c>docker cp</c> really brings the result file and
/// the round's tree back, and — the one this ticket inherited a warning about — that a
/// round the factory has given up on leaves no container behind.
/// </summary>
/// <remarks>
/// They are separated from the rest of the suite and skipped on a machine with no Docker
/// daemon or no worker image, so a development environment that cannot run them is not
/// broken by them. The one test that fetches a repository needs outbound network, which
/// is the same thing a round needs in production: a worker container is given no host
/// path, so the tree arrives over the network (ADR-0010).
/// </remarks>
[Collection("worker containers")]
public class WorkerRoundTests
{
    private const string RepoUrl = "https://github.com/octocat/Hello-World.git";
    private const string BaseRef = "master";

    /// <summary>
    /// A brief written the way a reviewer trying to break the factory would write one:
    /// every one of these is shell syntax, and none of it may be read as any.
    /// <c>printf EXE%sCUTED</c> is the sharpest of them, because the word
    /// <c>EXECUTED</c> can only appear in the log if a shell expanded it.
    /// </summary>
    private const string HostileBrief = "\"; rm -rf /; printf EXE%sCUTED \" `id -u` $(id -un) && echo '";

    [DockerFact]
    public async Task A_round_runs_in_a_real_container_and_its_result_and_tree_come_back_out()
    {
        using var workItem = new WorkItem();
        using var landing = new Landing();
        var said = new List<string>();
        var docker = new DockerCli(NullLogger<DockerCli>.Instance);
        var runtime = new ContainerRuntime(docker, NullLogger<ContainerRuntime>.Instance);

        var run = await UnderWay(
            runtime.RunAsync(
                new WorkerContainerRequest(
                    workItem.Container,
                    WorkerImage.Tag,
                    ["shell", "-c", WorkerRoundRunner.RoundScript],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["AGENT_FACTORY_REPO_URL"] = RepoUrl,
                        ["AGENT_FACTORY_BASE_REF"] = BaseRef,
                        ["AGENT_FACTORY_BRIEF"] = HostileBrief,
                    }),
                landing.Path,
                new Collect(said),
                CancellationToken.None),
            said);

        // The log came out as the round ran, and it is the image's own log channel
        // (ADR-0010). The entrypoint says what it cloned and where its result is, and the
        // round's own commands' output is in there.
        var log = string.Join("\n", said);
        Assert.Contains($"worker-round: cloning {RepoUrl} @ {BaseRef}", log, StringComparison.Ordinal);
        Assert.Contains("worker-round: result=", log, StringComparison.Ordinal);
        Assert.Contains("worker-collect: wrote /out/result.json", log, StringComparison.Ordinal);

        // The reviewer's words arrived as words. Had the brief been written into the
        // command, the container's own shell would have run all of it: the tree would be
        // gone, and EXECUTED would be in the log.
        Assert.Contains(HostileBrief, log, StringComparison.Ordinal);
        Assert.DoesNotContain("EXECUTED", log, StringComparison.Ordinal);
        Assert.Contains("`id -u`", log, StringComparison.Ordinal);

        // The result is one file, lifted out, and it is the image's own header line: the
        // round ran as the unprivileged user, holding no credential of any kind.
        Assert.NotNull(run.ResultFile);
        using var header = System.Text.Json.JsonDocument.Parse((await File.ReadAllTextAsync(run.ResultFile!)).Split('\n')[0]);
        var facts = header.RootElement;

        Assert.Equal("agent-factory/worker-result@1", facts.GetProperty("schema").GetString());
        Assert.Equal(1000, facts.GetProperty("uid").GetInt32());
        Assert.Empty(facts.GetProperty("credentialEnvNames").EnumerateArray());

        // The log channel and the result file agree about the commit the round ended on.
        // Two channels out of a round, and they are not allowed to disagree.
        var head = facts.GetProperty("head").GetString()!;
        Assert.Contains(head[..7], log, StringComparison.Ordinal);

        // And the round's tree came out too, with that same commit at its head (ADR-0006:
        // the host pushes, and it can only do that if it can reach the commit).
        Assert.NotNull(run.RoundTree);
        Assert.Equal("ref: refs/heads/master", (await File.ReadAllTextAsync(Path.Combine(run.RoundTree!, ".git", "HEAD"))).Trim());
        Assert.Contains(
            head,
            await File.ReadAllTextAsync(Path.Combine(run.RoundTree!, ".git", "packed-refs")),
            StringComparison.Ordinal);

        // Nothing of the round is left on the daemon: no container, no volume, no network.
        // A container that outlived its round is exactly what makes "nothing survives it"
        // untrue, and it is invisible from the board.
        Assert.Equal(string.Empty, (await docker.InvokeAsync(
            ["ps", "-a", "--filter", $"name={workItem.Container}", "--format", "{{.Names}}"],
            null,
            CancellationToken.None)).Output);

        Assert.Equal(string.Empty, (await docker.InvokeAsync(
            ["volume", "ls", "--filter", $"name={workItem.Container}", "--format", "{{.Name}}"],
            null,
            CancellationToken.None)).Output);

        Assert.Equal(string.Empty, (await docker.InvokeAsync(
            ["network", "ls", "--filter", $"name={workItem.Container}", "--format", "{{.Name}}"],
            null,
            CancellationToken.None)).Output);
    }

    [DockerFact]
    public async Task A_worker_container_publishes_no_port_mounts_nothing_and_is_removed_even_when_the_round_is_ended()
    {
        using var workItem = new WorkItem();
        using var landing = new Landing();
        using var rounds = new CancellationTokenSource();
        var docker = new DockerCli(NullLogger<DockerCli>.Instance);
        var runtime = new ContainerRuntime(docker, NullLogger<ContainerRuntime>.Instance);

        // A round that is still going when the factory stops waiting for it. This is the
        // trap this ticket inherited: the loop stops waiting on a timed-out round, and a
        // round that ignores its token keeps its container for ever.
        var underWay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var said = new List<string>();

        var running = runtime.RunAsync(
            new WorkerContainerRequest(
                workItem.Container,
                WorkerImage.Tag,
                ["shell", "-c", "set -uo pipefail\necho 'the round is under way'\nwhile true; do sleep 1; done"],
                new Dictionary<string, string>(StringComparer.Ordinal)),
            landing.Path,
            new Collect(said, underWay),
            rounds.Token);

        // The log reached the host while the round was still running, which is the only
        // moment the container's properties can be read off it.
        await UnderWay(underWay.Task, said);

        var live = (await docker.InvokeAsync(
            [
                "inspect",
                "--format",
                "{{len .Mounts}}|{{json .HostConfig.Binds}}|{{len .HostConfig.PortBindings}}"
                    + "|{{json .Config.ExposedPorts}}|{{.HostConfig.Privileged}}|{{json .HostConfig.CapAdd}}"
                    + "|{{.HostConfig.NetworkMode}}|{{.Config.User}}",
                workItem.Container,
            ],
            null,
            CancellationToken.None)).Output
            .Split('|')
            .Select(field => field.Trim())
            .ToArray();

        // The compensating controls ADR-0012 lists, read back off the container rather than
        // off the command that made it: no host path, no published port, no exposed port,
        // not privileged, no added capabilities, not on the host's network, unprivileged.
        Assert.Equal("0", live[0]);
        Assert.Equal("null", live[1]);
        Assert.Equal("0", live[2]);
        Assert.Equal("null", live[3]);
        Assert.Equal("false", live[4]);
        Assert.Equal("null", live[5]);
        Assert.Equal("bridge", live[6]);
        Assert.Equal("agent:agent", live[7]);

        await rounds.CancelAsync();

        // Ending the round ends the call, and it ends as a cancellation rather than as a
        // round that failed: the loop has already recorded this one as timed out.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UnderWay(running, said));

        // And the container is gone. This is the assertion the whole ticket turns on: a
        // round the factory gave up on must not leave a container holding its resources.
        var gone = await docker.InvokeAsync(
            ["inspect", workItem.Container],
            null,
            CancellationToken.None);

        Assert.NotEqual(0, gone.ExitCode);
        Assert.Equal(string.Empty, (await docker.InvokeAsync(
            ["ps", "-a", "--filter", $"name={workItem.Container}", "--format", "{{.Names}}"],
            null,
            CancellationToken.None)).Output);
    }

    /// <summary>
    /// Waits for something real to happen, and fails rather than hanging the suite if it
    /// does not. A real container round is a real wait, and a wait with no bound is a
    /// suite that stops telling you anything.
    /// </summary>
    private static async Task<T> UnderWay<T>(Task<T> task, List<string> said)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                "a real worker container did not finish inside two minutes. What reached the host "
                    + $"from its log was: {string.Join(" | ", said)}");
        }
    }

    private static async Task UnderWay(Task task, List<string> said)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                "a real worker container did not finish inside two minutes. What reached the host "
                    + $"from its log was: {string.Join(" | ", said)}");
        }
    }

    private static async Task<WorkerContainerRun> UnderWay(Task<WorkerContainerRun> task, List<string> said)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                "a real worker container did not finish inside two minutes. What reached the host "
                    + $"from its log was: {string.Join(" | ", said)}");
        }
    }

    /// <summary>
    /// A container name built from a work item, so teardown can find it and so a test can
    /// look for what is left of it. The same shape the round runner uses.
    /// </summary>
    private sealed class WorkItem : IDisposable
    {
        private readonly Guid _id = Guid.NewGuid();

        public string Container => $"agent-factory-round-{_id:N}";

        public void Dispose()
        {
            // Nothing of this test's should be left for the daemon to keep, whatever the
            // assertions above said. A test that cannot clean up behind itself is a test
            // that makes the next run of the suite lie.
            if (WorkerImage.Available)
            {
                WorkerImage.Docker("rm", "-f", Container);
            }
        }
    }

    /// <summary>Where a round's lifted-out files land, deleted on dispose.</summary>
    private sealed class Landing : IDisposable
    {
        public Landing()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agent-factory-rounds", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                // A tree lifted out of a Linux container arrives with the permissions it
                // had there, and a git pack file is read-only. Windows will not delete a
                // read-only file, so the attributes go first.
                foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
            catch (UnauthorizedAccessException)
            {
                // As above, for a file whose attributes could not be cleared.
            }
        }
    }

    /// <summary>Collects a round's log, and says when a particular line has arrived.</summary>
    private sealed class Collect(List<string> into, TaskCompletionSource? on = null) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (into)
            {
                into.Add(value);
            }

            if (on is not null && value.Contains("the round is under way", StringComparison.Ordinal))
            {
                on.TrySetResult();
            }
        }
    }
}
