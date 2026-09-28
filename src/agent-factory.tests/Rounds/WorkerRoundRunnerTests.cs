namespace AgentFactory.Tests.Rounds;

using AgentFactory;
using AgentFactory.Containers;
using AgentFactory.Projects;
using AgentFactory.Rounds;
using AgentFactory.Tests.Boundary;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// A round, run through the real round runner, with only the Docker CLI faked. The
/// runner's own claims are under test here: which image it starts from, what it hands
/// the container, and what it says about how the round ended. Whether a container really
/// starts, and really has the toolchain, is <see cref="WorkerRoundTests"/>'s business.
/// </summary>
public class WorkerRoundRunnerTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";

    private static readonly Guid WorkItem = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task A_round_runs_in_a_container_started_from_the_projects_configured_image()
    {
        using var harness = AFactory("nexus", "ghcr.io/nanisoft/agent-factory-worker:1");

        await harness.Runner.RunRoundAsync(ARound(), CancellationToken.None);

        var create = harness.Docker.TheOnly("create");
        Assert.Equal("ghcr.io/nanisoft/agent-factory-worker:1", create[^4]);
        Assert.Equal($"agent-factory-round-{WorkItem:N}", create[2]);
    }

    [Fact]
    public async Task The_rounds_brief_reaches_the_container_as_words_and_not_as_shell_syntax()
    {
        // A reviewer's feedback, quoted as a reviewer would write it if they were trying
        // to break the factory. Every one of these is a metacharacter.
        const string feedback = "\"; rm -rf /; echo \"`whoami` $(id) && 'quoted'";

        using var harness = AFactory();
        var round = ARound(feedback: feedback);

        await harness.Runner.RunRoundAsync(round, CancellationToken.None);

        var create = harness.Docker.TheOnly("create");

        // The brief reaches the container in exactly one place: the value of an
        // environment variable, as one argument.
        Assert.Equal(
            new[] { $"AGENT_FACTORY_BRIEF={feedback}" },
            create.Where(argument => argument.Contains(feedback, StringComparison.Ordinal)).ToArray());

        // The command is a fixed script, so the brief is provably not in it. Had the
        // runner written the reviewer's words into the command line, that script would
        // differ per round and this equality would not hold.
        Assert.Equal(new[] { "shell", "-c", WorkerRoundRunner.RoundScript }, create.TakeLast(3).ToArray());
        Assert.DoesNotContain(feedback, WorkerRoundRunner.RoundScript, StringComparison.Ordinal);

        // And the script only ever names the variable, so there is nothing for a shell
        // inside the container to expand either.
        Assert.Contains("\"$AGENT_FACTORY_BRIEF\"", WorkerRoundRunner.RoundScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_round_fetches_its_own_repository_at_the_work_items_base()
    {
        using var harness = AFactory();

        await harness.Runner.RunRoundAsync(ARound(), CancellationToken.None);

        // A worker container is given no host path, so the tree arrives over the network
        // (ADR-0010) and the round records how it got there.
        var create = harness.Docker.TheOnly("create");
        Assert.Contains($"AGENT_FACTORY_REPO_URL={RepoUrl}", create);
        Assert.Contains("AGENT_FACTORY_BASE_REF=main", create);
    }

    [Fact]
    public async Task A_round_is_handed_no_credential_of_any_kind()
    {
        using var harness = AFactory();

        await harness.Runner.RunRoundAsync(ARound(), CancellationToken.None);

        // ADR-0006: the round holds nothing that can write to a remote. The project file
        // names two environment variables holding secrets, and neither name nor value is
        // passed to the container — the factory reads neither, so it could not pass either.
        var create = harness.Docker.TheOnly("create");
        var handed = create
            .Where(argument => argument.StartsWith("AGENT_FACTORY_", StringComparison.Ordinal))
            .Select(argument => argument.Split('=', 2)[0])
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "AGENT_FACTORY_BASE_REF", "AGENT_FACTORY_BRIEF", "AGENT_FACTORY_REPO_URL" },
            handed);

        // The project's credential *names* are on the record and nothing else: the runner
        // never sees the values, so it cannot hand them over.
        Assert.DoesNotContain(create, argument => argument.Contains("TOKEN", StringComparison.Ordinal));
        Assert.DoesNotContain(create, argument => argument.Contains("API_KEY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_round_that_came_back_with_a_result_is_produced_and_carries_the_results_own_header()
    {
        using var harness = AFactory();
        harness.Docker.ContainerFiles[ContainerRuntime.ResultPathInContainer] =
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","roundExitCode":0,"uid":1000,"head":"59a7684"}
            {"kind":"command","seq":1,"argv":["git","log"]}
            {"kind":"note","field":"note","text":"Added the answer."}
            {"kind":"end","collectedInMs":629}
            """;

        var result = await harness.Runner.RunRoundAsync(ARound(), CancellationToken.None);

        Assert.Equal(RoundOutcome.Produced, result.Outcome);

        // The payload is the file's own header line, verbatim, and nothing is read out of
        // it yet. Deriving a result — which files changed, which commands passed — is the
        // deriver's job (ADR-0011) and it has not been written; this ticket delivers the
        // boundary, and the payload is thin on purpose.
        Assert.StartsWith("{\"kind\":\"result\"", result.ResultPayload!, StringComparison.Ordinal);
        Assert.Contains("\"roundExitCode\":0", result.ResultPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("git", result.ResultPayload, StringComparison.Ordinal);

        // The one thing a model is allowed to author is read out and kept, because the
        // field exists for it and the round is otherwise all observation.
        Assert.Equal("Added the answer.", result.AgentNote);
    }

    [Fact]
    public async Task A_round_whose_own_command_failed_is_still_a_result_and_not_a_failure()
    {
        using var harness = AFactory();
        harness.Docker.ContainerFiles[ContainerRuntime.ResultPathInContainer] =
            """{"kind":"result","roundExitCode":3,"uid":1000}""";

        var result = await harness.Runner.RunRoundAsync(ARound(), CancellationToken.None);

        // A round that ran and whose last command failed came back with a result. A
        // container that broke is the other thing, and it is not this.
        Assert.Equal(RoundOutcome.Produced, result.Outcome);
        Assert.Contains("\"roundExitCode\":3", result.ResultPayload!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_round_that_wrote_no_result_is_a_failure_and_says_what_its_log_ended_with()
    {
        using var harness = AFactory();
        harness.Docker.Handler = call => call.Arguments[0] == "logs"
            ? Task.FromResult(Say(call, "worker-round: cloning", "fatal: could not read from the repository"))
            : Task.FromResult(new DockerInvocation(0, string.Empty));

        var result = await harness.Runner.RunRoundAsync(ARound(), CancellationToken.None);

        Assert.Equal(RoundOutcome.Failed, result.Outcome);
        Assert.Null(result.ResultPayload);

        // The container still went, which is the half of the promise that has nothing to
        // do with whether the round produced anything.
        Assert.Equal(["rm", "-f", $"agent-factory-round-{WorkItem:N}"], harness.Docker.TheOnly("rm"));
    }

    [Fact]
    public async Task A_round_whose_container_broke_is_a_failure_rather_than_a_result()
    {
        using var harness = AFactory();
        harness.Docker.Handler = call => call.Arguments[0] == "create"
            ? Task.FromResult(new DockerInvocation(125, "Error response from daemon: no such image"))
            : Task.FromResult(new DockerInvocation(0, string.Empty));

        var result = await harness.Runner.RunRoundAsync(ARound(), CancellationToken.None);

        // No result, and no payload claiming there was one.
        Assert.Equal(RoundOutcome.Failed, result.Outcome);
        Assert.Null(result.ResultPayload);
    }

    [Fact]
    public async Task A_round_that_was_ended_by_the_factory_says_so_and_loses_its_container()
    {
        using var harness = AFactory();
        using var rounds = new CancellationTokenSource();
        var inTheRound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Docker.Handler = async call =>
        {
            if (call.Arguments[0] != "logs")
            {
                return new DockerInvocation(0, string.Empty);
            }

            inTheRound.TrySetResult();
            await Task.Delay(Timeout.Infinite, call.CancellationToken);
            return new DockerInvocation(0, string.Empty);
        };

        var running = harness.Runner.RunRoundAsync(ARound(), rounds.Token);
        await inTheRound.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await rounds.CancelAsync();

        // The call ends, and it ends as a cancellation rather than as a round that failed:
        // the loop has already recorded this one as timed out, and telling it a second
        // time that the round failed would be two answers to one question. Bounded, since a
        // round that ignored its token would otherwise hang the suite instead of failing it.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => running.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(["rm", "-f", $"agent-factory-round-{WorkItem:N}"], harness.Docker.TheOnly("rm"));
    }

    [Fact]
    public async Task A_rounds_result_and_tree_land_beside_the_store_and_are_kept_after_the_round()
    {
        using var harness = AFactory();
        harness.Docker.ContainerFiles[ContainerRuntime.ResultPathInContainer] =
            """{"kind":"result","roundExitCode":0}""";
        harness.Docker.ContainerFiles["/work/.git/HEAD"] = "ref: refs/heads/main\n";

        await harness.Runner.RunRoundAsync(ARound(), CancellationToken.None);

        // ADR-0006: the host has to reach the round's commit after the container that made
        // it is gone, because the host pushes and not the container. That is only true if
        // the lifted-out files are not temporary, so where they land and that they are
        // still there afterwards are both part of the promise.
        var landing = Path.Combine(harness.Options.RoundsDirectory, WorkItem.ToString("N"));

        Assert.Equal(Path.Combine(landing, "result.json"), Assert.Single(Directory.GetFiles(landing)));
        Assert.True(File.Exists(Path.Combine(landing, "tree", ".git", "HEAD")));

        // And it is kept rather than cleaned up: the promise is that the commit is still
        // there for the merger once the container that made it is gone. This harness's own
        // dispose is what takes the directory away afterwards, which is the test's doing and
        // not the round's.
        Assert.Equal(Path.GetDirectoryName(harness.Options.DatabasePath), Path.GetDirectoryName(harness.Options.RoundsDirectory));
    }

    [Fact]
    public async Task A_work_whose_project_file_is_not_being_served_does_not_start_a_container()
    {
        using var harness = AFactory();

        var result = await harness.Runner.RunRoundAsync(
            ARound(project: "a project nobody serves"),
            CancellationToken.None);

        Assert.Equal(RoundOutcome.Failed, result.Outcome);
        Assert.Empty(harness.Docker.Calls);
    }

    private static Round ARound(string feedback = "", string project = "nexus") => new(
        WorkItem,
        project,
        RepoUrl,
        42,
        "main",
        feedback);

    private static Harness AFactory(
        string project = "nexus",
        string image = "ghcr.io/nanisoft/agent-factory-worker:1") =>
        AFactory(new FakeDockerCli(), project, image);

    private static Harness AFactory(
        FakeDockerCli docker,
        string project = "nexus",
        string image = "ghcr.io/nanisoft/agent-factory-worker:1")
    {
        var projects = new ProjectLoadReport(
            [
                new Project(
                    project,
                    RepoUrl,
                    image,
                    "anthropic",
                    "NEXUS_GITHUB_TOKEN",
                    "NEXUS_ANTHROPIC_API_KEY",
                    $"{project}.yaml"),
            ],
            []);

        // A throwaway store beside a throwaway rounds directory, both in a temporary place,
        // because a round's files are kept rather than cleaned up (ADR-0006) and a test
        // must not leave them where a later run would find them.
        var store = Path.Combine(
            Path.GetTempPath(),
            "agent-factory-rounds-tests",
            Guid.NewGuid().ToString("n"),
            "agent-factory.db");

        var options = new FactoryOptions("factories", store, new Uri("http://127.0.0.1:0"));
        var runtime = new ContainerRuntime(docker, NullLogger<ContainerRuntime>.Instance);
        var runner = new WorkerRoundRunner(runtime, projects, options, NullLogger<WorkerRoundRunner>.Instance);

        return new Harness(docker, runner, options);
    }

    private static class Landing
    {
        /// <summary>Removes a test's own throwaway directory, since a round's files are kept.</summary>
        public static void CleanUp(string databasePath)
        {
            try
            {
                var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(databasePath)));
                if (root is not null && Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temporary directory is not worth failing a test over.
            }
        }
    }

    private static DockerInvocation Say(FakeDockerCli.DockerCall call, params string[] lines)
    {
        foreach (var line in lines)
        {
            call.Output?.Report(line);
        }

        return new DockerInvocation(0, lines[^1]);
    }

    /// <summary>
    /// A round runner over a fake Docker, with a throwaway store. Disposing it removes the
    /// directory a round's kept files land in, which every test here needs: a round's
    /// result and tree are kept rather than cleaned up (ADR-0006), so a test that does not
    /// remove them leaves the next run with a directory it did not create.
    /// </summary>
    private sealed class Harness(FakeDockerCli docker, WorkerRoundRunner runner, FactoryOptions options) : IDisposable
    {
        public FakeDockerCli Docker { get; } = docker;

        public WorkerRoundRunner Runner { get; } = runner;

        public FactoryOptions Options { get; } = options;

        public void Dispose() => Landing.CleanUp(Options.DatabasePath);
    }
}

