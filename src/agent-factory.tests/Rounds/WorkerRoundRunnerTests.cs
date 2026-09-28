namespace AgentFactory.Tests.Rounds;

using AgentFactory;
using AgentFactory.Containers;
using AgentFactory.Failures;
using AgentFactory.Projects;
using AgentFactory.Results;
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
        // environment variable, as one argument. A brief that also carries the whole issue
        // and the project is still one argument, and this is the assertion that holds when
        // it does.
        var carried = Assert.Single(create, argument => argument.Contains(feedback, StringComparison.Ordinal));
        Assert.StartsWith("AGENT_FACTORY_BRIEF=", carried, StringComparison.Ordinal);

        // The command is a fixed script, so the brief is provably not in it. Had the
        // runner written the reviewer's words into the command line, that script would
        // differ per round and this equality would not hold.
        Assert.Equal(new[] { "shell", "-c", WorkerRoundRunner.RoundScript }, create.TakeLast(3).ToArray());
        Assert.DoesNotContain(feedback, WorkerRoundRunner.RoundScript, StringComparison.Ordinal);

        // And the script only ever names the variable, so there is nothing for a shell
        // inside the container to expand either. The brief is written out to a file and
        // handed to the CLI with `--file`, so even the CLI never receives the reviewer's
        // words as a command line of its own.
        Assert.Contains("\"$AGENT_FACTORY_BRIEF\"", WorkerRoundRunner.RoundScript, StringComparison.Ordinal);
        Assert.Contains("printf '%s' \"$AGENT_FACTORY_BRIEF\"", WorkerRoundRunner.RoundScript, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "-- \"$AGENT_FACTORY_BRIEF\"",
            WorkerRoundRunner.RoundScript,
            StringComparison.Ordinal);
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
    public async Task A_round_is_handed_the_projects_llm_credential_and_never_its_github_one()
    {
        // The sharp line this ticket has to keep. Two credentials are named in the project
        // file and they are not the same kind of thing: the LLM key is what the agent
        // reaches a provider with and has to be in the container, and the GitHub key is the
        // one thing that can write to a remote and must never be (ADR-0006). Both are
        // configured here, so a runner that passed the wrong one would be caught.
        var credentials = new FakeCredentialReader()
            .Having("NEXUS_ANTHROPIC_API_KEY", "llm-secret-value")
            .Having("NEXUS_GITHUB_TOKEN", "github-secret-value");

        using var harness = AFactory(credentials: credentials);

        await harness.Runner.RunRoundAsync(ARound(), CancellationToken.None);

        var create = harness.Docker.TheOnly("create");

        // The LLM key is in, because the agent cannot reach a provider without it. Its
        // value is present exactly once, in one argument, and nowhere else.
        Assert.Contains("NEXUS_ANTHROPIC_API_KEY=llm-secret-value", create);
        Assert.Equal(1, create.Count(argument => argument.Contains("llm-secret-value", StringComparison.Ordinal)));

        // The GitHub key is out, and the check is that its *value* was never even read:
        // the runner has no way to hand over what it never picked up, which is what makes
        // ADR-0006 structural rather than a matter of care.
        Assert.DoesNotContain(create, argument => argument.Contains("github-secret-value", StringComparison.Ordinal));
        Assert.DoesNotContain("NEXUS_GITHUB_TOKEN", credentials.Asked);
    }

    [Fact]
    public async Task A_round_is_handed_the_projects_own_environment_for_the_agents_benefit_and_nothing_else()
    {
        // Everything the runner sets is either the round's own machinery or a value derived
        // from the work item. A test that read the names alone would miss a sixth
        // environment variable handing something over.
        var credentials = new FakeCredentialReader().Having("NEXUS_ANTHROPIC_API_KEY", "llm-secret-value");
        using var harness = AFactory(credentials: credentials);

        await harness.Runner.RunRoundAsync(ARound(), CancellationToken.None);

        var handed = harness.Docker.TheOnly("create")
            .Where(argument => argument.StartsWith("AGENT_FACTORY_", StringComparison.Ordinal)
                || argument.StartsWith("NEXUS_", StringComparison.Ordinal))
            .Select(argument => argument.Split('=', 2)[0])
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "AGENT_FACTORY_AGENT_PROMPT",
                "AGENT_FACTORY_BASE_REF",
                "AGENT_FACTORY_BRIEF",
                "AGENT_FACTORY_REPO_URL",
                "NEXUS_ANTHROPIC_API_KEY",
            },
            handed);
    }

    [Fact]
    public async Task A_round_whose_project_llm_credential_is_not_configured_still_runs_and_says_so()
    {
        // A project names a credential it does not have. The honest outcomes are to run the
        // round and say the credential was missing, or to refuse — and refusing would be the
        // factory deciding for itself that the agent needs one, which is the agent's call
        // and not the factory's. What is not acceptable is silence: a round handed nothing
        // that says nothing reads on a board as a round that needed nothing.
        var credentials = new FakeCredentialReader();
        using var harness = AFactory(credentials: credentials);

        await harness.Runner.RunRoundAsync(ARound(), CancellationToken.None);

        var create = harness.Docker.TheOnly("create");
        Assert.DoesNotContain("NEXUS_ANTHROPIC_API_KEY", create);
        Assert.Equal(["NEXUS_ANTHROPIC_API_KEY"], credentials.Asked);
    }

    [Fact]
    public async Task A_rounds_brief_carries_the_issue_the_project_and_the_reviewers_own_words()
    {
        using var harness = AFactory();
        var round = ARound(
            feedback: "the parser drops the last field",
            title: "The parser drops the last field",
            body: "Parsing a line of CSV loses whatever came after the final comma.");

        await harness.Runner.RunRoundAsync(round, CancellationToken.None);

        var brief = Assert.Single(
            harness.Docker.TheOnly("create"),
            argument => argument.StartsWith("AGENT_FACTORY_BRIEF=", StringComparison.Ordinal))["AGENT_FACTORY_BRIEF=".Length..];

        // The issue, not the issue number. A round handed a number has to go and look up
        // what it means, and a brief that does not carry the maintainer's own words is the
        // factory paraphrasing the work it was asked to do.
        Assert.Contains("The parser drops the last field", brief, StringComparison.Ordinal);
        Assert.Contains("Parsing a line of CSV loses whatever came after the final comma.", brief, StringComparison.Ordinal);
        Assert.Contains("nexus", brief, StringComparison.Ordinal);
        Assert.Contains(RepoUrl, brief, StringComparison.Ordinal);

        // The reviewer's reasons, kept whole and in their own words.
        Assert.Contains("the parser drops the last field", brief, StringComparison.Ordinal);

        // And the brief tells the agent to commit and stop, because the host pushes and
        // the container cannot (ADR-0006). The repository's own scripts decide what testing
        // means; the factory does not get an opinion (ADR-0011).
        Assert.Contains("decide what running its tests means", brief, StringComparison.Ordinal);
        Assert.Contains("commit your work to the branch you are on and stop there", brief, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_round_drives_the_agent_non_interactively_inside_its_own_container()
    {
        using var harness = AFactory();

        await harness.Runner.RunRoundAsync(ARound(), CancellationToken.None);

        var create = harness.Docker.TheOnly("create");

        // `opencode run` is the non-interactive form (ADR-0004), and the brief reaches it
        // as a *file* rather than as a command line, so nothing a reviewer wrote can be
        // read as anything but words however the CLI handles it.
        Assert.Contains("opencode run", WorkerRoundRunner.RoundScript, StringComparison.Ordinal);
        Assert.Contains("--standalone", WorkerRoundRunner.RoundScript, StringComparison.Ordinal);
        Assert.Contains("--file \"$out/brief.md\"", WorkerRoundRunner.RoundScript, StringComparison.Ordinal);

        // `--auto` is a deliberate choice and worth being explicit about: without it the
        // CLI stops and asks a human to approve every edit, and there is no human inside a
        // worker container. What it widens is the agent's freedom *within* the container —
        // which holds no credential that can write to a remote, is given no host path and
        // publishes no port (ADR-0006, ADR-0010, ADR-0012). There is nothing outside the
        // container for a confused or injected agent to reach, which is exactly the
        // containment the design bought.
        Assert.Contains("--auto", WorkerRoundRunner.RoundScript, StringComparison.Ordinal);

        // The prompt is the factory's own fixed sentence and points at the brief rather
        // than quoting it, so the command line a reviewer can see is identical for every
        // round whatever issue it is building.
        Assert.Contains(
            $"AGENT_FACTORY_AGENT_PROMPT={WorkerRoundRunner.AgentPrompt}",
            create);
        Assert.Equal(["shell", "-c", WorkerRoundRunner.RoundScript], create.TakeLast(3).ToArray());
    }

    [Fact]
    public async Task A_round_that_came_back_with_a_result_is_produced_and_carries_a_derived_record()
    {
        using var harness = AFactory();
        harness.Docker.ContainerFiles[ContainerRuntime.ResultPathInContainer] =
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","roundExitCode":0,"uid":1000,"user":"agent","gitRepo":true,"branch":"main","startHead":"aaa1111","head":"bbb2222","credentialEnvNames":[]}
            {"kind":"command","seq":1,"label":"the tests","argv":["./scripts/test.sh"],"exitCode":0,"stdoutTail":"1 passed\n"}
            {"kind":"git","field":"status","text":"# branch.head main\n1 .M N... 100644 100644 100644 a b c src/Index.cs\n"}
            {"kind":"git","field":"diffFromRoundStart","text":"diff --git a/src/Index.cs b/src/Index.cs\n--- a/src/Index.cs\n+++ b/src/Index.cs\n@@ -1 +1 @@\n-old\n+new\n"}
            {"kind":"note","field":"note","text":"Added the answer."}
            {"kind":"end","collectedInMs":629}
            """;

        var result = await harness.Runner.RunRoundAsync(ARound(), CancellationToken.None);

        Assert.Equal(RoundOutcome.Produced, result.Outcome);

        // The payload is a record derived from what the container observed, not the file's
        // own bytes. ADR-0011: the factory reads the records and reshapes them into
        // something a reviewer can judge, and the header line it used to hand back
        // verbatim named none of the three things the design asks for.
        Assert.DoesNotContain("\"kind\":\"result\"", result.ResultPayload!, StringComparison.Ordinal);
        Assert.Contains("src/Index.cs", result.ResultPayload, StringComparison.Ordinal);
        Assert.Contains("./scripts/test.sh", result.ResultPayload, StringComparison.Ordinal);
        Assert.Contains("1 passed", result.ResultPayload, StringComparison.Ordinal);

        // The one thing a model is allowed to author is read out and kept, because the
        // field exists for it and the round is otherwise all observation.
        Assert.Equal("Added the answer.", result.AgentNote);
    }

    [Fact]
    public async Task A_round_whose_own_commands_failed_is_still_a_result_and_not_a_failure()
    {
        using var harness = AFactory();
        harness.Docker.ContainerFiles[ContainerRuntime.ResultPathInContainer] =
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","roundExitCode":3,"uid":1000,"gitRepo":true}
            {"kind":"command","seq":1,"label":"the tests","argv":["./scripts/test.sh"],"exitCode":1,"stderrTail":"expected 42, got 0\n"}
            """;

        var result = await harness.Runner.RunRoundAsync(ARound(), CancellationToken.None);

        // A round that ran and whose own commands failed came back with a result, and the
        // failing exit code is data inside it for a reviewer to read. A container that
        // broke is the other thing, and it is not this — which is the structural half of
        // "a build that fails its tests is not retried" (ADR-0001).
        Assert.Equal(RoundOutcome.Produced, result.Outcome);
        Assert.Null(result.Failure);
        Assert.False(result.IsRetryable);

        // The failure is in the record, said as a number and as what the command said.
        Assert.Contains("exit 1", result.ResultPayload!, StringComparison.Ordinal);
        Assert.Contains("expected 42, got 0", result.ResultPayload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rounds_files_changed_come_from_its_own_git_state_and_not_from_the_agents_note()
    {
        // End to end through the runner rather than only through the deriver: the claim is
        // that a round's *result* reflects the container, and this is the boundary the
        // result crosses on its way onto the board. The note claims three files; git saw
        // one of them, and only that one is in the payload.
        using var harness = AFactory();
        harness.Docker.ContainerFiles[ContainerRuntime.ResultPathInContainer] =
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","roundExitCode":0,"gitRepo":true,"uid":1000}
            {"kind":"git","field":"status","text":"# branch.head main\n1 .M N... 100644 100644 100644 a b c src/Index.cs\n"}
            {"kind":"git","field":"diffFromRoundStart","text":"diff --git a/src/Index.cs b/src/Index.cs\n--- a/src/Index.cs\n+++ b/src/Index.cs\n@@ -1 +1 @@\n-old\n+new\n"}
            {"kind":"note","field":"note","text":"Rewrote src/Index.cs, README.md and deleted LICENSE."}
            """;

        var result = await harness.Runner.RunRoundAsync(ARound(), CancellationToken.None);

        // The payload is one text, so the check has to be about *where* a path appears and
        // not merely whether it appears: the note is kept verbatim and therefore does
        // contain the two paths the agent claimed. What matters is that the files-changed
        // section — everything above the agent's own account — names only what git saw.
        var derived = result.ResultPayload!;
        var agentOwnAccount = derived.IndexOf("the agent's own account", StringComparison.Ordinal);
        Assert.True(agentOwnAccount > 0, "the payload separates what was observed from what the agent said");

        var observed = derived[..agentOwnAccount];
        Assert.Contains("src/Index.cs", observed, StringComparison.Ordinal);
        Assert.DoesNotContain("README.md", observed, StringComparison.Ordinal);
        Assert.DoesNotContain("LICENSE", observed, StringComparison.Ordinal);

        // The note is still there, verbatim, below the line. It is kept as the agent's
        // account of itself and is derived from by nothing.
        Assert.Equal("Rewrote src/Index.cs, README.md and deleted LICENSE.", result.AgentNote);
    }

    [Fact]
    public async Task A_round_that_wrote_no_result_is_a_permanent_failure_and_carries_the_log_a_reviewer_needs()
    {
        using var harness = AFactory();
        harness.Docker.Handler = call => call.Arguments[0] == "logs"
            ? Task.FromResult(Say(call, "worker-round: cloning", "fatal: could not read from the repository"))
            : Task.FromResult(new DockerInvocation(0, string.Empty));

        var result = await harness.Runner.RunRoundAsync(ARound(), CancellationToken.None);

        Assert.Equal(RoundOutcome.Failed, result.Outcome);

        // Permanent, and this is the classification the whole retry policy turns on. The
        // container started, ran and produced nothing: the round's own work failed, and a
        // second container would be told the same thing. A round that is worth another
        // attempt is the other shape of failure — a container that never ran — and the two
        // used to arrive here in exactly the same way.
        Assert.Equal(FailureClass.Permanent, result.Failure);
        Assert.False(result.IsRetryable);
        Assert.Null(result.ResultPayload);

        // The log is on the result rather than only in the factory's own logging. This is
        // the half of story 28 the old shape could not do: a round with no payload at all
        // used to be invisible on the board, and there was nowhere to point a reviewer.
        Assert.Contains("fatal: could not read from the repository", result.Log!, StringComparison.Ordinal);

        // The container still went, which is the half of the promise that has nothing to do
        // with whether the round produced anything.
        Assert.Equal(["rm", "-f", $"agent-factory-round-{WorkItem:N}"], harness.Docker.TheOnly("rm"));
    }

    [Fact]
    public async Task A_round_whose_result_could_not_be_read_degrades_to_showing_the_log()
    {
        // Story 29, and the shape the old thin payload made impossible to satisfy: a result
        // file that came back with nothing readable in it. The round still happened, so
        // there is still something to show, and showing nothing would be the worst of the
        // three possible answers.
        using var harness = AFactory();
        harness.Docker.ContainerFiles[ContainerRuntime.ResultPathInContainer] =
            "this is not a result file at all";

        var result = await harness.Runner.RunRoundAsync(ARound(), CancellationToken.None);

        // Produced, not Failed: the container ran and its result file came back, and what
        // is wrong with it is a fact about the round rather than a failure the loop retries.
        Assert.Equal(RoundOutcome.Produced, result.Outcome);
        Assert.Null(result.AgentNote);
        Assert.Contains("could not be read", result.ResultPayload!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_round_whose_result_is_not_at_all_still_carries_its_log_onto_the_result()
    {
        using var harness = AFactory();
        harness.Docker.Handler = call => call.Arguments[0] == "logs"
            ? Task.FromResult(Say(call, "worker-round: cloning", "the round said something"))
            : Task.FromResult(new DockerInvocation(0, string.Empty));

        var result = await harness.Runner.RunRoundAsync(ARound(), CancellationToken.None);

        // A permanent failure, and the log is the whole of what a reviewer has — so it
        // travels on the result rather than being left in a log line (story 28, 29).
        Assert.Equal(RoundOutcome.Failed, result.Outcome);
        Assert.Contains("the round said something", result.Log!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_round_whose_container_broke_is_a_transient_failure_rather_than_a_result()
    {
        using var harness = AFactory();
        harness.Docker.Handler = call => call.Arguments[0] == "create"
            ? Task.FromResult(new DockerInvocation(125, "Error response from daemon: no such image"))
            : Task.FromResult(new DockerInvocation(0, string.Empty));

        var result = await harness.Runner.RunRoundAsync(ARound(), CancellationToken.None);

        // No result, and no payload claiming there was one.
        Assert.Equal(RoundOutcome.Failed, result.Outcome);
        Assert.Null(result.ResultPayload);

        // Transient, and for the reason the classification lives here rather than above:
        // this runner is the component that knows the round never started, and a `create`
        // that could not pull an image is a daemon that was not answering rather than a
        // change that does not build. Worth another attempt; the loop decides how many.
        Assert.Equal(FailureClass.Transient, result.Failure);
        Assert.True(result.IsRetryable);
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

        // Permanent, and unlike a container that would not start this one cannot change:
        // configuration loads at start and does not hot reload, so a second attempt would
        // start no container either.
        Assert.Equal(FailureClass.Permanent, result.Failure);
        Assert.False(result.IsRetryable);
    }

    private static Round ARound(
        string feedback = "",
        string project = "nexus",
        string title = "A work item, end to end",
        string body = "What the issue says, in the maintainer's words.") => new(
        WorkItem,
        project,
        RepoUrl,
        42,
        title,
        body,
        "main",
        feedback);

    private static Harness AFactory(
        string project = "nexus",
        string image = "ghcr.io/nanisoft/agent-factory-worker:1",
        FakeCredentialReader? credentials = null) =>
        AFactory(new FakeDockerCli(), project, image, credentials);

    private static Harness AFactory(
        FakeDockerCli docker,
        string project = "nexus",
        string image = "ghcr.io/nanisoft/agent-factory-worker:1",
        FakeCredentialReader? credentials = null)
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
        var runner = new WorkerRoundRunner(
            runtime,
            projects,
            options,
            new RoundResultDeriver(NullLogger<RoundResultDeriver>.Instance),
            credentials ?? new FakeCredentialReader(),
            NullLogger<WorkerRoundRunner>.Instance);

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

