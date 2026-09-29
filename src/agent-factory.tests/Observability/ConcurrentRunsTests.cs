namespace AgentFactory.Tests.Observability;

using AgentFactory.Containers;
using AgentFactory.GitHub;
using AgentFactory.Observability;
using AgentFactory.Results;
using AgentFactory.Rounds;
using AgentFactory.Tests.Boundary;
using AgentFactory.Tests.Review;
using AgentFactory.WorkItems;
using Microsoft.Extensions.Logging;

/// <summary>
/// Two work items at once, and the claim that their records do not blur together.
/// </summary>
/// <remarks>
/// <para>
/// This is the criterion that concurrency makes hard, and the trap in it is the shape of
/// the factory rather than anything in the log line: two rounds for different work items run
/// at the same time, and a work item can itself have several rounds, so a record keyed only
/// on the project, or only on the swimlane, or only on "a round finished", answers none of
/// the questions a reader has. "What happened to <em>this</em> work item" is only answerable
/// if every record carries the work item — and separable only if the two never share a key.
/// </para>
/// <para>
/// The interleaving is real rather than simulated. Two projects, two work items, both rounds
/// held open by the fake agent at once, and the budget is two — so the two rounds genuinely
/// overlap, records from both are written while each is in flight, and each is released in
/// the other's lifetime. A test that ran them one after another would pass against a factory
/// whose records could not be told apart.
/// </para>
/// </remarks>
public class ConcurrentRunsTests
{
    private const string Nexus = "nexus";
    private const string Atlas = "atlas";
    private const string NexusRepo = "https://github.com/NaniSoft/nexus";
    private const string AtlasRepo = "https://github.com/NaniSoft/atlas";

    [Fact]
    public async Task Two_work_items_running_at_once_leave_separable_records()
    {
        using var root = FactoryRoot.Create()
            .WithProjectFile("atlas.yaml", ProjectFile.For(Atlas, AtlasRepo))
            .WithProjectFile("nexus.yaml", ProjectFile.For(Nexus, NexusRepo));

        var log = new RecordedLog();
        var counters = new FactoryMetrics();
        using var measurements = new RecordedMeasurements(counters);

        await using var host = await FactoryHost.RecordingAsync(
            root, log, counters, agent: new FakeNOpenCode(), github: new FakeGitHub());

        host.GitHub
            .WithRepository(NexusRepo, "main", new OpenIssue(1, "the nexus issue", "build it", [], []))
            .WithRepository(AtlasRepo, "main", new OpenIssue(2, "the atlas issue", "build this", [], []));

        // Intake takes one project's turn at a time, so both work items exist after a whole
        // pass — which is also what proves the rotation, but the assertion here is about the
        // records those two work items then leave.
        await host.PollAsync();
        Assert.Equal(2, host.Store.List().Count);

        // Both rounds are held open at once. `Stuck` is the fake's way of saying the round
        // does not come back on its own, and two of them with a budget of two is the most
        // overlap this factory can be inside.
        host.Agent.Stuck().Stuck();
        await host.Settle();

        Assert.Equal(2, host.RoundsInFlight);

        // The one record that proves they overlapped rather than queued: both "started"
        // records are on the board before either round has come back.
        var started = log.Records.Where(record => record.Values("Busy").Count > 0).ToList();
        Assert.Equal(2, started.Count);
        Assert.Equal([1, 2], started.Select(record => (int)record.Field("Busy")!).Order().ToList());

        // Released one at a time, and the second comes back while the first has already been
        // recorded — so the two histories interleave in the output and are still separable.
        // `Settle` rather than `RunTheMachineAsync`, because the machine is *meant* to be
        // holding a round here: the point is that one of the two has landed while the other
        // has not.
        host.Agent.Release();
        await host.Settle();
        Assert.Equal(1, host.RoundsInFlight);

        host.Agent.Release();
        await host.Settle();
        Assert.Equal(0, host.RoundsInFlight);

        var work = host.Store.List().ToDictionary(item => item.IssueNumber);

        // **Every record belongs to exactly one work item, and neither record claims both.**
        // Not "the records are in the right order" and not "the messages mention the right
        // repository": each record's own identity field resolves to one work item, and the
        // two sets are disjoint.
        var forOne = log.About(work[1].Id);
        var forTwo = log.About(work[2].Id);

        Assert.NotEmpty(forOne);
        Assert.NotEmpty(forTwo);
        Assert.DoesNotContain(forOne, record => record.Guids(WorkItemScope.WorkItemIdKey).Contains(work[2].Id));
        Assert.DoesNotContain(forTwo, record => record.Guids(WorkItemScope.WorkItemIdKey).Contains(work[1].Id));

        // **And each work item's own story is whole on its own.** This is what "do not blur
        // together" has to mean: the records about one work item, read alone, say which
        // project it was for, which issue, which round, and how that round came back — with
        // nothing from the other work item in the way.
        foreach (var (issue, item) in work)
        {
            var mine = log.About(item.Id);

            Assert.All(mine, record =>
            {
                Assert.Contains(item.Id, record.Guids(WorkItemScope.WorkItemIdKey));
                Assert.Equal(item.Project, record.Field(WorkItemScope.ProjectKey));
                Assert.Equal(issue, record.Field(WorkItemScope.IssueKey));
            });

            var round = Assert.Single(mine, record => record.Values("Outcome").Count > 0);
            Assert.Equal(1, round.Field("Round"));
            Assert.Equal(nameof(AgentFactory.Rounds.RoundOutcome.Produced), round.Field("Outcome")?.ToString());
        }

        // The two projects are distinguishable in the records, and each one only ever names
        // its own — which is the other half of the trap, because a project is the coarsest
        // key available and two work items of one project would be indistinguishable under it.
        Assert.Equal(
            [Atlas],
            log.About(work[2].Id).Select(record => record.Field(WorkItemScope.ProjectKey)).Distinct().ToList());
        Assert.Equal(
            [Nexus],
            log.About(work[1].Id).Select(record => record.Field(WorkItemScope.ProjectKey)).Distinct().ToList());

        // And the metrics agree with both, without naming either: two rounds, one per
        // project, and the only labelled thing is the project rather than the work item.
        Assert.Equal(2, measurements.Count(FactoryMetrics.RoundsName, nameof(AgentFactory.Rounds.RoundOutcome.Produced)));
        Assert.Equal(
            [Atlas, Nexus],
            measurements.TagValues.Where(value => value is Atlas or Nexus).Distinct().Order().ToList());
    }

    [Fact]
    public async Task A_record_written_inside_a_round_names_the_work_item_although_nothing_underneath_it_knows_one()
    {
        // The scope's claim is stronger than "the loop's own records are stamped": a component
        // handed no work item at all still writes records that name one, because it is running
        // inside a scope. Here those components are the real ones — the result deriver, the
        // container runtime and the Docker CLI — behind the real round runner, with only the
        // Docker CLI faked.
        //
        // Without the scope the deriver's constructor would have to grow a work item id, and
        // `PolicyTests` pins that constructor to a single logger; and a record the deriver
        // wrote an hour into a round could not be attributed to anything at all. Both are the
        // alternative this design refuses, and this is what it costs instead: nothing.
        using var tree = LiftedTree.WithACommitOn();
        tree.Committed("add the endpoint", ("src/Endpoint.cs", "public sealed class Endpoint { }\n"));
        tree.WithReadOnlyObjects();

        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);

        var docker = new FakeDockerCli();
        docker.ContainerTrees[ContainerRuntime.WorkPathInContainer] = tree.Path;
        docker.ContainerFiles[ContainerRuntime.ResultPathInContainer] = ResultFile(tree.StartHead);

        var log = new RecordedLog();
        var counters = new FactoryMetrics();
        using var measurements = new RecordedMeasurements(counters);

        await using var host = await FactoryHost.RecordingWithTheRealRoundAsync(root, docker, log, counters);
        var work = host.Store
            .Intake(Nexus, NexusRepo, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;

        // Not Settle: the real round runner copies a tree off disk and runs git against it,
        // so the round is genuinely in flight across calls and the machine has to be stepped
        // until it holds nothing.
        await host.RunTheMachineAsync();

        Assert.Equal(Swimlane.Review, host.Store.Get(work.Id)!.Swimlane);

        var records = log.About(work.Id);

        // Records from a component that was handed nothing but a file path and a logger.
        // The deriver is the sharpest case in the process: its constructor is one
        // parameter, and `PolicyTests` names that constructor — so this is the assertion
        // that a round's records can be attributed without widening the seam the design
        // pinned. The record it wrote here is the count of lines it could not read, which
        // is ADR-0011's own named failure said out loud rather than inferred.
        var deriver = Assert.Single(records, record =>
            record.Category.EndsWith("RoundResultDeriver", StringComparison.Ordinal));

        Assert.Equal(1, deriver.Field("Lines"));
        Assert.Equal(LogLevel.Warning, deriver.Level);

        // Every record in the round names the work item and the round it was for, and the
        // round number is the one the loop asked for rather than one any of these
        // components had to be told.
        Assert.All(records, record =>
        {
            Assert.Equal(work.Id, record.Field(WorkItemScope.WorkItemIdKey));
            Assert.Equal(Nexus, record.Field(WorkItemScope.ProjectKey));
            Assert.Equal(42, record.Field(WorkItemScope.IssueKey));
            Assert.Equal(1, record.Field(WorkItemScope.RoundKey));
        });

        // The round's own account of what it produced is on the record as counts, so a
        // reviewer asking "what did this round change" gets numbers rather than prose — and
        // the round's log is not among them, because a ninety-minute build's output does not
        // belong in a log record.
        var derived = Assert.Single(records, record => record.Values("Files").Count > 0);
        Assert.Equal(1, derived.Field("Files"));
        Assert.Equal(1, derived.Field("Commands"));
        Assert.Equal(1, derived.Field("DiffFiles"));
        Assert.Equal(1, derived.Field(WorkItemScope.RoundKey));
    }

    [Fact]
    public async Task A_derivers_record_names_the_work_item_even_with_nothing_above_the_round_runner()
    {
        // The half of the scope claim the previous test cannot reach, and it is the half that
        // matters. Driven through the host, a round runs inside the loop's scope, so a record
        // the deriver wrote would carry the work item whether or not the round runner opened
        // one of its own — which makes that test unable to tell the two designs apart.
        //
        // So the runner is called directly, with nothing above it but a logger. The deriver
        // is constructed with a logger and one thing else, and `PolicyTests` pins that
        // constructor; the runner is the only component in the process handed a work item's
        // identity, and this is the assertion that it passes that identity on by being in a
        // scope rather than by telling each of its collaborators about it.
        var log = new RecordedLog();
        var docker = new FakeDockerCli();
        docker.ContainerFiles[ContainerRuntime.ResultPathInContainer] = TruncatedResultFile();

        var workItem = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var options = new FactoryOptions(
            "factories",
            Path.Combine(Path.GetTempPath(), "agent-factory-rounds-tests", Guid.NewGuid().ToString("n"), "agent-factory.db"),
            new Uri("http://127.0.0.1:0"));

        try
        {
            var runner = new WorkerRoundRunner(
                new ContainerRuntime(docker, log.For<ContainerRuntime>()),
                new AgentFactory.Projects.ProjectLoadReport(
                    [new AgentFactory.Projects.Project(
                        Nexus,
                        NexusRepo,
                        "ghcr.io/nanisoft/agent-factory-worker:1",
                        "anthropic",
                        "NEXUS_GITHUB_TOKEN",
                        "NEXUS_ANTHROPIC_API_KEY",
                        "nexus.yaml")],
                    []),
                options,
                new RoundResultDeriver(log.For<RoundResultDeriver>()),
                new FakeCredentialReader(),
                log.For<WorkerRoundRunner>());

            await runner.RunRoundAsync(
                new Round(workItem, Nexus, NexusRepo, 42, "the issue", "the brief", "main", string.Empty),
                CancellationToken.None);

            // The deriver wrote a record, and it names a work item it was never given.
            var deriver = Assert.Single(log.About(workItem), record =>
                record.Category.EndsWith("RoundResultDeriver", StringComparison.Ordinal));

            Assert.Equal(1, deriver.Field("Lines"));
            Assert.Equal(LogLevel.Warning, deriver.Level);

            // And nothing in the round needed the round number to say which round it was:
            // called directly there is no round number to have, and the identity is still
            // whole. In production the round number rides on the loop's scope, on top of this
            // one, which is what makes a record deep inside a round name both.
            Assert.DoesNotContain(
                log.About(workItem),
                record => record.Values(WorkItemScope.RoundKey).Count > 0);
        }
        finally
        {
            try
            {
                var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath)));
                if (root is { } && Directory.Exists(root))
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

    /// <summary>
    /// A result file in the shape <c>worker-collect</c> writes, carrying the commit the
    /// round started from so the host's diff reader has a base to work from.
    /// </summary>
    private static string ResultFile(string startHead) => string.Join(
        "\n",
        [
            $$"""{"kind":"result","schema":"agent-factory/worker-result@1","user":"agent","uid":1000,"gitRepo":true,"branch":"main","startHead":"{{startHead}}","head":"{{startHead}}","credentialEnvNames":[],"git":"git version 2.43.0","opencode":"opencode v2.0.18","os":"Ubuntu 24.04"}""",
            """{"kind":"git","field":"status","text":"1 A. N... 100644 100644 100644 0000000 1111111 src/Endpoint.cs"}""",
            """{"kind":"git","field":"diffFromRoundStart","text":"diff --git a/src/Endpoint.cs b/src/Endpoint.cs\nnew file mode 100644\n--- /dev/null\n+++ b/src/Endpoint.cs\n@@ -0,0 +1 @@\n+public sealed class Endpoint { }"}""",
            """{"kind":"git","field":"commitsSinceRoundStart","text":""}""",
            """{"kind":"git","field":"remotes","text":"origin\thttps://github.com/NaniSoft/nexus.git (fetch)"}""",
            """{"kind":"command","seq":1,"label":"the unit tests","argv":["dotnet","test"],"cwd":"/work","exitCode":0,"durationMs":1800,"stdoutTail":"42 passed, 0 failed\n","stderrTail":"","stdoutBytes":21,"stderrBytes":0}""",
            """{"kind":"note","text":"Added the endpoint, because nothing was answering."}""",

            // A truncated line — the collector was killed mid-write. It is here on purpose:
            // the design names "a results payload that parses but is missing the field the
            // reviewer needs" as the failure only integration catches, the deriver counts
            // what it could not read rather than skipping it silently, and the count is
            // announced — which is a record the deriver writes without having been told
            // which work item it was reading for.
            """{"kind":"command","seq":1,"label":"the build that was cut off""",
        ]);

    /// <summary>
    /// The same file with no tree behind it, so the host's diff reader has nothing to read
    /// and the round's own records are the whole of what there is to look at.
    /// </summary>
    private static string TruncatedResultFile() => string.Join(
        "\n",
        [
            """{"kind":"result","schema":"agent-factory/worker-result@1","user":"agent","uid":1000,"gitRepo":true,"branch":"main","startHead":"abc1234","head":"abc1234","credentialEnvNames":[],"git":"git version 2.43.0","opencode":"opencode v2.0.18","os":"Ubuntu 24.04"}""",
            """{"kind":"command","seq":1,"label":"the unit tests","argv":["dotnet","test"],"cwd":"/work","exitCode":0,"durationMs":1800,"stdoutTail":"42 passed, 0 failed\n","stderrTail":"","stdoutBytes":21,"stderrBytes":0}""",
            """{"kind":"command","seq":2,"label":"the build that was cut off""",
        ]);
}
