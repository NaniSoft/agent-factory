namespace AgentFactory.Tests.Containers;

using AgentFactory.Containers;
using AgentFactory.Tests.Boundary;

/// <summary>
/// The container runtime, with the Docker CLI faked and nothing else. What is under test
/// is the shape of the commands the runtime builds and the promise it makes about
/// teardown: one container per round, no port, no host path, and removal on every way
/// out including cancellation.
/// </summary>
/// <remarks>
/// These are hermetic on purpose. The real daemon is exercised in
/// <see cref="WorkerRoundTests"/>, and the image itself in <see cref="WorkerImageTests"/>;
/// what a fake cannot see is whether a container really starts, not whether the factory
/// asked for the right one.
/// </remarks>
public class ContainerRuntimeTests
{
    private static readonly Guid WorkItem = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task A_container_starts_from_the_projects_image_and_is_removed_afterwards()
    {
        using var landing = new Landing();
        var docker = new FakeDockerCli();
        docker.ContainerFiles[ContainerRuntime.ResultPathInContainer] = "{\"kind\":\"result\"}\n";
        var runtime = new ContainerRuntime(docker, Microsoft.Extensions.Logging.Abstractions.NullLogger<ContainerRuntime>.Instance);

        var run = await runtime.RunAsync(ARequest(), landing.Path, null, CancellationToken.None);

        // The image the project file names, and the command handed to the image's own
        // entrypoint as separate arguments rather than a shell string.
        var create = docker.TheOnly("create");
        var name = $"agent-factory-round-{WorkItem:N}";
        Assert.Equal(["--name", name], create.Skip(1).Take(2));
        Assert.Equal(["shell", "-c", "run git status"], create.TakeLast(3));
        Assert.Equal("ghcr.io/nanisoft/agent-factory-worker:1", create[^4]);

        // The round's environment, by name and value, as the image reads it.
        Assert.Contains("AGENT_FACTORY_REPO_URL=https://github.com/NaniSoft/nexus", create);
        Assert.Contains("AGENT_FACTORY_BASE_REF=main", create);

        // The whole of ADR-0010's protocol, in the order it happens: create it, start it,
        // read the log it is writing, lift the one file out, lift the tree out, and take
        // the container away.
        Assert.Equal(
            ["create", "start", "logs", "cp", "cp", "rm"],
            docker.Argvs.Select(argv => argv[0]));

        Assert.Equal(["start", name], docker.TheOnly("start"));
        Assert.Equal(["logs", "-f", name], docker.TheOnly("logs"));
        Assert.Equal(["rm", "-f", name], docker.Argv(5));
        Assert.NotNull(run.ResultFile);
    }

    [Fact]
    public async Task A_worker_container_publishes_no_port_and_mounts_no_host_path()
    {
        using var landing = new Landing();
        var docker = new FakeDockerCli();
        var runtime = new ContainerRuntime(docker, Microsoft.Extensions.Logging.Abstractions.NullLogger<ContainerRuntime>.Instance);

        await runtime.RunAsync(ARequest(), landing.Path, null, CancellationToken.None);

        // "Publishes no inbound port" and "mounts no host path" are properties of the
        // command the factory builds, not of the image alone, so they are asserted on
        // every argv rather than on the one that seems most likely to carry them.
        var forbidden = new[] { "-p", "-P", "--publish", "-v", "--volume", "--mount", "--privileged", "--cap-add", "--network", "--network-alias", "--ipc", "--pid" };
        foreach (var argv in docker.Argvs)
        {
            foreach (var argument in argv)
            {
                Assert.DoesNotContain(forbidden, flag => argument.StartsWith(flag, StringComparison.Ordinal));
            }
        }

        // And the only host paths that appear in any command at all are the destinations
        // of the two `docker cp` calls. Anywhere else, the host's filesystem is not named.
        var hostPaths = docker.Argvs
            .Where(argv => argv[0] != "cp")
            .SelectMany(argv => argv)
            .Where(argument => argument.Contains(landing.Path, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(hostPaths);
    }

    [Fact]
    public async Task The_rounds_log_arrives_on_the_host_while_the_round_is_still_running()
    {
        using var landing = new Landing();
        var docker = new FakeDockerCli
        {
            Handler = call => call.Arguments[0] == "logs"
                ? Task.FromResult(Say(call, "worker-round: startHead=abc123", "the first test passed", "the last thing the round said"))
                : Task.FromResult(new DockerInvocation(0, string.Empty)),
        };

        var said = new List<string>();
        var runtime = new ContainerRuntime(docker, Microsoft.Extensions.Logging.Abstractions.NullLogger<ContainerRuntime>.Instance);

        var run = await runtime.RunAsync(ARequest(), landing.Path, new Collect(said), CancellationToken.None);

        // ADR-0010 makes the log the channel carrying live progress, so it is the
        // progress channel and not the returned result that has to have it.
        Assert.Equal(["worker-round: startHead=abc123", "the first test passed", "the last thing the round said"], said);

        // And the end of it is kept, bounded, for the log line that comes after the round
        // is over. A 90-minute round's whole log does not belong in memory.
        Assert.EndsWith("the last thing the round said", run.LogTail, StringComparison.Ordinal);
        Assert.True(run.LogTail.Length <= 8192, "the tail is bounded rather than the whole log");
    }

    [Fact]
    public async Task The_result_leaves_the_container_as_one_file_lifted_with_docker_cp()
    {
        using var landing = new Landing();
        var docker = new FakeDockerCli();
        docker.ContainerFiles[ContainerRuntime.ResultPathInContainer] = "{\"kind\":\"result\"}\n";
        var runtime = new ContainerRuntime(docker, Microsoft.Extensions.Logging.Abstractions.NullLogger<ContainerRuntime>.Instance);

        var run = await runtime.RunAsync(ARequest(), landing.Path, null, CancellationToken.None);

        // One file, one `cp`, out of the factory's own output directory rather than out of
        // the working tree — a result inside the tree would show up in the diff the
        // reviewer is judging.
        var lift = docker.Argvs.Single(argv => argv[0] == "cp"
            && argv.Any(argument => argument.EndsWith(ContainerRuntime.ResultPathInContainer, StringComparison.Ordinal)));
        Assert.Equal($"agent-factory-round-{WorkItem:N}:{ContainerRuntime.ResultPathInContainer}", lift[1]);
        Assert.Equal(landing.Path, Path.GetDirectoryName(lift[2]));

        Assert.Equal("{\"kind\":\"result\"}\n", await File.ReadAllTextAsync(run.ResultFile!));
    }

    [Fact]
    public async Task The_rounds_tree_is_lifted_out_so_that_the_host_can_reach_its_commit()
    {
        using var landing = new Landing();
        var docker = new FakeDockerCli();
        docker.ContainerFiles["/work/.git/HEAD"] = "ref: refs/heads/main\n";
        var runtime = new ContainerRuntime(docker, Microsoft.Extensions.Logging.Abstractions.NullLogger<ContainerRuntime>.Instance);

        var run = await runtime.RunAsync(ARequest(), landing.Path, null, CancellationToken.None);

        // ADR-0006: the round ends at a commit and the host pushes, so the host has to be
        // able to get at that commit after the container that made it is gone.
        Assert.Equal(Path.Combine(landing.Path, "tree"), run.RoundTree);
        Assert.True(File.Exists(Path.Combine(run.RoundTree!, ".git", "HEAD")));
    }

    [Fact]
    public async Task A_round_that_wrote_no_result_leaves_nothing_lifted_and_still_loses_its_container()
    {
        using var landing = new Landing();
        var docker = new FakeDockerCli
        {
            // The container ran and stopped without a result file, which is the round that
            // degrades to its log rather than to nothing (story 29).
            Handler = call => call.Arguments is ["cp", ..] && call.Arguments[1].EndsWith(ContainerRuntime.ResultPathInContainer, StringComparison.Ordinal)
                ? Task.FromResult(new DockerInvocation(1, "Error: no such file"))
                : Task.FromResult(new DockerInvocation(0, string.Empty)),
        };

        var runtime = new ContainerRuntime(docker, Microsoft.Extensions.Logging.Abstractions.NullLogger<ContainerRuntime>.Instance);

        var run = await runtime.RunAsync(ARequest(), landing.Path, null, CancellationToken.None);

        Assert.Null(run.ResultFile);
        Assert.Equal(["rm", "-f", $"agent-factory-round-{WorkItem:N}"], docker.Argv(5));
    }

    [Fact]
    public async Task A_container_that_could_not_be_created_is_removed_and_says_so()
    {
        using var landing = new Landing();
        var docker = new FakeDockerCli
        {
            Handler = call => call.Arguments[0] == "create"
                ? Task.FromResult(new DockerInvocation(125, "Error response from daemon: no such image"))
                : Task.FromResult(new DockerInvocation(0, string.Empty)),
        };

        var runtime = new ContainerRuntime(docker, Microsoft.Extensions.Logging.Abstractions.NullLogger<ContainerRuntime>.Instance);

        // A container that never existed is a round that never happened, which is a
        // different thing from a round that ran and failed, and it says which.
        var refused = await Assert.ThrowsAsync<WorkerContainerException>(
            () => runtime.RunAsync(ARequest(), landing.Path, null, CancellationToken.None));

        Assert.Contains("create", refused.Message, StringComparison.Ordinal);
        Assert.Contains("no such image", refused.Message, StringComparison.Ordinal);

        // Even a container that was never created is asked for by name, because the name
        // is the one handle that survives losing the create's own output.
        Assert.Equal(["rm", "-f", $"agent-factory-round-{WorkItem:N}"], docker.TheOnly("rm"));
    }

    [Fact]
    public async Task A_rounds_log_tail_survives_being_reported_to_from_both_streams_at_once()
    {
        using var landing = new Landing();
        var docker = new FakeDockerCli();
        var said = new List<string>();

        // `docker logs` splits the round's own output across two of its streams, so this
        // receiver is called from two threads at once. The tail it keeps is what a failed
        // round is explained by, and a tail that drops or tears lines says something the
        // round never said.
        docker.Handler = call =>
        {
            if (call.Arguments[0] != "logs")
            {
                return Task.FromResult(new DockerInvocation(0, string.Empty));
            }

            return Task.Run(() =>
            {
                Parallel.For(0, 4_000, index =>
                {
                    if (index % 2 == 0)
                    {
                        call.Output?.Report($"out-{index:D5}-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                    }
                    else
                    {
                        call.Output?.Report($"err-{index:D5}-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
                    }
                });

                return new DockerInvocation(0, string.Empty);
            });
        };

        var runtime = new ContainerRuntime(docker, Microsoft.Extensions.Logging.Abstractions.NullLogger<ContainerRuntime>.Instance);

        var run = await runtime.RunAsync(ARequest(), landing.Path, new Collect(said), CancellationToken.None);

        // Nothing was lost on the way to the caller, and nothing is torn.
        Assert.Equal(4_000, said.Count);
        Assert.Contains("out-00000-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", said);
        Assert.Contains("err-03999-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", said);
        Assert.All(said, line => Assert.DoesNotContain('\0', line));

        // And the tail is whole lines, all of them ones the round really said.
        var tail = run.LogTail.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(tail);
        Assert.All(tail, line => Assert.Contains(line, said));
    }

    [Fact]
    public async Task A_cancelled_round_ends_the_call_and_leaves_no_container_running()
    {
        using var landing = new Landing();
        using var rounds = new CancellationTokenSource();
        var inTheRound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var docker = new FakeDockerCli
        {
            // A round that is still going when the factory stops waiting for it. The fake
            // honours the token, exactly as the real CLI does, so what is under test here
            // is the runtime's teardown and not the fake's cooperation.
            Handler = async call =>
            {
                if (call.Arguments[0] != "logs")
                {
                    return new DockerInvocation(0, string.Empty);
                }

                call.Output?.Report("worker-round: still working");
                inTheRound.TrySetResult();
                await Task.Delay(Timeout.Infinite, call.CancellationToken);
                return new DockerInvocation(0, string.Empty);
            },
        };

        var runtime = new ContainerRuntime(docker, Microsoft.Extensions.Logging.Abstractions.NullLogger<ContainerRuntime>.Instance);

        var running = runtime.RunAsync(ARequest(), landing.Path, null, rounds.Token);

        // The round is genuinely under way before it is ended, rather than the token being
        // cancelled before the container existed — which would prove nothing about a
        // round that had started.
        await inTheRound.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(["create", "start", "logs"], docker.Argvs.Select(argv => argv[0]));

        await rounds.CancelAsync();

        // The call ends rather than hanging: the loop stops waiting for a round, and this
        // is what it is waiting on. Bounded, because a round that ignores its token is
        // exactly the failure this test exists to catch, and a test that hangs on it
        // reports nothing at all.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => running.WaitAsync(TimeSpan.FromSeconds(30)));

        // The teardown happened anyway, and it was not cancellable — a token that is
        // already cancelled must not be able to stop the container being removed, which is
        // the trap this ticket inherited.
        var removal = docker.Calls.Single(call => call.Arguments[0] == "rm");
        Assert.Equal(["rm", "-f", $"agent-factory-round-{WorkItem:N}"], removal.Arguments);
        Assert.False(removal.CancellationToken.CanBeCanceled, "teardown runs on a token nobody can cancel");
    }

    [Fact]
    public async Task A_container_is_removed_even_when_lifting_the_result_fails()
    {
        using var landing = new Landing();
        var docker = new FakeDockerCli
        {
            Handler = call => call.Arguments[0] == "cp"
                ? Task.FromResult(new DockerInvocation(1, "Error: something went wrong"))
                : Task.FromResult(new DockerInvocation(0, string.Empty)),
        };

        var runtime = new ContainerRuntime(docker, Microsoft.Extensions.Logging.Abstractions.NullLogger<ContainerRuntime>.Instance);

        // No exception: a result that could not be lifted is a round without a result,
        // and the loop's answer to that is a record rather than a crash. The container
        // goes either way.
        var run = await runtime.RunAsync(ARequest(), landing.Path, null, CancellationToken.None);

        Assert.Null(run.ResultFile);
        Assert.Null(run.RoundTree);
        Assert.Equal(["rm", "-f", $"agent-factory-round-{WorkItem:N}"], docker.TheOnly("rm"));
    }

    [Fact]
    public async Task A_container_is_removed_when_the_call_it_answered_throws()
    {
        using var landing = new Landing();
        var docker = new FakeDockerCli
        {
            Handler = call => call.Arguments[0] == "logs"
                ? throw new IOException("the log stream broke")
                : Task.FromResult(new DockerInvocation(0, string.Empty)),
        };

        var runtime = new ContainerRuntime(docker, Microsoft.Extensions.Logging.Abstractions.NullLogger<ContainerRuntime>.Instance);

        await Assert.ThrowsAsync<IOException>(() => runtime.RunAsync(ARequest(), landing.Path, null, CancellationToken.None));

        Assert.Equal(["rm", "-f", $"agent-factory-round-{WorkItem:N}"], docker.TheOnly("rm"));
    }

    private static WorkerContainerRequest ARequest() => new(
        $"agent-factory-round-{WorkItem:N}",
        "ghcr.io/nanisoft/agent-factory-worker:1",
        ["shell", "-c", "run git status"],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AGENT_FACTORY_REPO_URL"] = "https://github.com/NaniSoft/nexus",
            ["AGENT_FACTORY_BASE_REF"] = "main",
        });

    private static DockerInvocation Say(FakeDockerCli.DockerCall call, params string[] lines)
    {
        foreach (var line in lines)
        {
            call.Output?.Report(line);
        }

        return new DockerInvocation(0, lines[^1]);
    }

    /// <summary>
    /// Collects a round's log. Locked, because <see cref="IDockerCli"/> reports from both
    /// of a command's streams at once and a collector that is not thread-safe drops lines
    /// of its own — which would make this file's assertions about loss prove nothing.
    /// </summary>
    private sealed class Collect(List<string> into) : IProgress<string>
    {
        private readonly Lock _lock = new();

        public void Report(string value)
        {
            lock (_lock)
            {
                into.Add(value);
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
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }
}
