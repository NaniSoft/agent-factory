namespace AgentFactory.Tests.Concurrency;

using AgentFactory;
using AgentFactory.Credentials;
using AgentFactory.Driving;
using AgentFactory.Domain.Failures;
using AgentFactory.Domain.Rounds;
using AgentFactory.GitHub;
using AgentFactory.Projects;
using AgentFactory.Results;
using AgentFactory.Rounds;
using AgentFactory.Tests.Boundary;
using AgentFactory.Tests.Review;
using AgentFactory.WorkItems;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The round concurrency, and the driver that spends it. Several projects make progress at
/// once, bounded by a count of rounds rather than by a lock, so one noisy
/// repository cannot starve the rest and a long build in one project does not stop
/// another's issue from starting (DESIGN.md, ADR-0001).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here sleeps, and nothing here waits for a round to be slow.</b> Every
/// concurrency question in this file is settled by holding a round open and stepping the
/// machine, never by timing one: a round that has been asked for and not yet returned is a
/// round the factory is inside, so <c>FakeNOpenCode</c> counts how many were inside at
/// once and the tests assert on that count. A test that waited for the work to finish would
/// pass just as well against a factory that ran everything one at a time, which is the
/// whole difference this ticket exists to draw.
/// </para>
/// <para>
/// The ceiling is asserted as a peak, not as an end state. Every work item reaching Review
/// is what a serial factory also does, and proving the bound needs the number of rounds
/// that existed at the same time.
/// </para>
/// </remarks>
public class RoundConcurrencyTests
{
    private const string Nexus = "https://github.com/NaniSoft/nexus";
    private const string Alpha = "https://github.com/NaniSoft/alpha";
    private const string IssueBody = "What the issue says, in the maintainer's words.";

    [Fact]
    public async Task The_ceiling_bounds_how_many_rounds_run_at_once()
    {
        // The number written out rather than read from the constant: a test that read it
        // would prove only that the loop compares two values to each other.
        Assert.Equal(2, FactoryConstants.RoundConcurrency);

        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Stuck().Stuck().Stuck();
        await using var host = await FactoryHost.StartAsync(root, agent: agent);

        var first = await Take(host, Nexus, 42, "nexus");
        var second = await Take(host, Nexus, 43, "nexus");
        var third = await Take(host, Nexus, 44, "nexus");

        await host.Settle();

        // The peak is the assertion. Two rounds existed at the same time — not two rounds
        // that had each existed — and a factory that had run them one after another while
        // reaching the same end state would report a peak of one and fail here.
        Assert.Equal(2, agent.PeakConcurrency);
        Assert.Equal(2, agent.AskedFor.Count);
        Assert.Equal(2, host.RoundsInFlight);

        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, first.Id));
        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, second.Id));

        // The third is not refused and not running: it is queued, in Frontier, which is the
        // lane a reviewer can see. The acceptance criterion is that it waits rather than
        // starting without a slot, and a work item in Backlog would be a work item that
        // had not been accepted at all.
        Assert.Equal(Swimlane.Frontier, SwimlaneOf(host, third.Id));
        Assert.DoesNotContain(agent.AskedFor, round => round.WorkItemId == third.Id);
    }

    [Fact]
    public async Task The_ceiling_is_never_exceeded_no_matter_how_many_work_items_are_waiting()
    {
        // The same bound, held across a long run of rounds rather than observed once, so
        // that an implementation which released a slot too early — or never released one —
        // fails somewhere in the middle instead of only at the start.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode();
        for (var round = 0; round < 10; round++)
        {
            agent.Stuck();
        }

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        for (var issue = 1; issue <= 5; issue++)
        {
            await Take(host, Nexus, issue, "nexus");
        }

        // Drained a wave at a time: five work items, a ceiling of two, and a queue behind
        // them. The peak is the whole of the claim about the bound and it is checked on
        // every wave, so an implementation that released a slot too early fails in the
        // middle of the run rather than only at the end.
        var peak = 0;
        var waves = 0;
        while (agent.AskedFor.Count < 5)
        {
            await host.Settle();
            peak = Math.Max(peak, agent.PeakConcurrency);

            // Every held round comes back, and the queue behind it moves into the space each
            // one leaves. Nothing sleeps: a held round is released by the test, and the
            // next wave is the next step. Releasing until none is left rather than a fixed
            // count, because five work items and a ceiling of two do not divide evenly and a
            // test that assumed they did would be testing arithmetic.
            while (agent.Release() > 0)
            {
            }

            Assert.True(waves++ < 20, "the queue stopped draining, which no ceiling should cause");
        }

        Assert.Equal(2, peak);
        Assert.Equal(2, FactoryConstants.RoundConcurrency);
        Assert.Equal(5, agent.AskedFor.Count);

        // And the machine is idle at the end, having spent exactly five rounds on five work
        // items: one round per work item per round of the ceiling, never two rounds on one
        // work item and never a third alongside them.
        await host.Settle();
        Assert.Equal(0, host.RoundsInFlight);
        Assert.Equal(5, agent.AskedFor.Select(round => round.WorkItemId).Distinct().Count());
    }

    [Fact]
    public async Task A_long_build_in_one_project_does_not_delay_another_projects_issue_from_starting()
    {
        // The criterion from DESIGN.md, and the one that is easiest to write a test for and
        // hardest to write a real one for: a round that will not come back must not stop
        // another project's issue from starting.
        //
        // No timing is involved. Alpha's first issue is held open and never released, which
        // is what a long build looks like from here; the assertion is that beta's issue got
        // a round anyway, and that the two were inside the factory at the same time.
        using var root = FactoryRoot.Create()
            .WithProjectFile("alpha.yaml", ProjectFile.For("alpha", Alpha))
            .WithProjectFile("nexus.yaml", ProjectFile.For("nexus", Nexus));
        var agent = new FakeNOpenCode().Stuck().Stuck();
        await using var host = await FactoryHost.StartAsync(root, agent: agent);

        var slow = await Take(host, Alpha, 1, "alpha");
        var quick = await Take(host, Nexus, 1, "nexus");

        await host.Settle();

        // Both are running, and both are running *now* — the peak of two is what says the
        // second did not have to wait for the first.
        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, slow.Id));
        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, quick.Id));
        Assert.Equal(2, agent.PeakConcurrency);
        Assert.Equal(
            ["alpha", "nexus"],
            agent.AskedFor.Select(round => round.Project));

        // And it is not a queue that happens to be working through: alpha's round is still
        // held open at the end, and beta's never was.
        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, slow.Id));
        Assert.Single(agent.AskedFor, round => round.Project == "alpha");
    }

    [Fact]
    public async Task A_noisy_repository_cannot_take_the_whole_ceiling_from_a_project_with_one_issue()
    {
        // The ceiling on its own bounds the machine; it does not stop a project with a deep
        // queue from taking both slots and holding them for ever while a project with
        // a single issue waits behind a thousand older ones. The rule that stops that is
        // that the next round goes to whichever project is holding fewer.
        //
        // Alpha has three issues and beta has one, all created at the same moment, so
        // nothing here is about age: it is about which project the ceiling is spent on.
        using var root = FactoryRoot.Create()
            .WithProjectFile("alpha.yaml", ProjectFile.For("alpha", Alpha))
            .WithProjectFile("nexus.yaml", ProjectFile.For("nexus", Nexus));
        var agent = new FakeNOpenCode().Stuck().Stuck();
        await using var host = await FactoryHost.StartAsync(root, agent: agent);

        var first = await Take(host, Alpha, 1, "alpha");
        var second = await Take(host, Alpha, 2, "alpha");
        var third = await Take(host, Alpha, 3, "alpha");
        var elsewhere = await Take(host, Nexus, 1, "nexus");

        await host.Settle();

        // The first round went to the oldest work item, which is alpha's. The second
        // went to beta, because beta was holding none and alpha was holding one — a
        // thousand more alpha issues would not change that.
        Assert.Equal(
            ["alpha", "nexus"],
            agent.AskedFor.Select(round => round.Project));
        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, first.Id));
        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, elsewhere.Id));
        Assert.Equal(Swimlane.Frontier, SwimlaneOf(host, second.Id));
        Assert.Equal(Swimlane.Frontier, SwimlaneOf(host, third.Id));
    }

    [Fact]
    public async Task One_project_on_its_own_still_gets_the_whole_ceiling()
    {
        // The other half of the fairness rule, and the one that would be broken by taking
        // "one round per project" too literally: a project with a queue is not held to
        // one round while a second sits idle. This is what a single project looks like
        // in development, and it must fill the ceiling.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Stuck().Stuck();
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        await Take(host, Nexus, 1, "nexus");
        await Take(host, Nexus, 2, "nexus");

        await host.Settle();

        Assert.Equal(2, agent.PeakConcurrency);
        Assert.Equal(2, agent.AskedFor.Select(round => round.WorkItemId).Distinct().Count());
    }

    [Fact]
    public async Task A_reviewers_approval_merges_with_the_ceiling_full()
    {
        // The shape #6 left for this ticket, and the answer is that a merge takes no slot
        // at all. A merge starts no round, runs no agent and costs nothing the ceiling
        // exists to protect — so making it wait for one would let a build in one project
        // decide when a change reaches another repository, and a factory with two long
        // builds running would stop shipping what a human had already read and approved.
        // That is a less safe factory than one with no builds at all.
        //
        // Both slots are genuinely held open by rounds that have not come back, so the
        // approval below is made with the ceiling full and stuck. Nothing about the timing
        // is a race: the rounds are stuck, are never released, and stay stuck throughout.
        var clock = new TestClock();
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().Merging();
        var agent = new FakeNOpenCode()
            .Producing("src/Index.cs +12 -3", "First attempt.")
            .Stuck()
            .Stuck();
        await using var host = await FactoryHost.StartAsync(root, clock, agent, github);

        var approved = await Take(host, Nexus, 42, "nexus");
        await host.Settle();
        Assert.Equal(Swimlane.Review, SwimlaneOf(host, approved.Id));

        // Two more work items, and the ceiling fills with rounds that do not come back.
        await Take(host, Nexus, 43, "nexus");
        await Take(host, Nexus, 44, "nexus");
        await host.Settle();

        Assert.Equal(2, host.RoundsInFlight);
        Assert.Equal(2, agent.PeakConcurrency);
        Assert.Equal(2, agent.BeingHeld);

        // A reviewer approves while both rounds are running, and the change ships. This is
        // the whole assertion: a merge gated on a slot would leave this sitting in Review
        // with a reviewer's click apparently lost, and the reviewer would have no way to
        // tell that from the factory simply being slow.
        using var response = await Board.DecideAsync(host.Board, approved.Id, "approve");
        Assert.Null((await Board.ReadAsync(response)).Refusal());

        Assert.Equal([new MergeAttempt(Nexus, 42)], github.Merges);
        Assert.Equal(Swimlane.Done, SwimlaneOf(host, approved.Id));

        // And the merge took nothing from the ceiling and nothing from the rounds: both are
        // still running, which is what a merge that consumed no slot looks like.
        Assert.Equal(2, host.RoundsInFlight);
        Assert.Equal(2, agent.BeingHeld);
    }

    [Fact]
    public async Task A_retried_merge_is_not_blocked_by_a_full_ceiling_either()
    {
        // The third way in, and the one with the shortest schedule of the three: a parked
        // work item's bounded merge retry has a ten-second backoff, so unlike the feedback
        // threshold it can absolutely come due while two rounds are still building. A gate
        // on the ceiling would silently stop it, and a parked work item whose merge would
        // have landed is a change a factory is sitting on.
        var clock = new TestClock();
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().RefusingToMerge(FailureClass.Transient, "the API returned 503");
        var agent = new FakeNOpenCode()
            .Producing("src/Index.cs +12 -3", "First attempt.")
            .Stuck()
            .Stuck();
        await using var host = await FactoryHost.StartAsync(root, clock, agent, github);

        var parked = await Take(host, Nexus, 42, "nexus");
        await host.Settle();
        Assert.Equal(Swimlane.Review, SwimlaneOf(host, parked.Id));

        using (await Board.DecideAsync(host.Board, parked.Id, "approve"))
        {
        }

        // Parked with a merge failure of its own, which is what makes it eligible for a
        // second attempt at all.
        Assert.Equal(Swimlane.Escalated, SwimlaneOf(host, parked.Id));
        Assert.Single(github.Merges);

        // The ceiling fills, and then the backoff passes.
        await Take(host, Nexus, 43, "nexus");
        await Take(host, Nexus, 44, "nexus");
        await host.Settle();
        Assert.Equal(2, host.RoundsInFlight);

        github.Merging();
        clock.Advance(FactoryConstants.RetryBackoff(1));
        await host.Settle();

        // The retry was made and it landed, with both rounds running throughout.
        Assert.Equal(2, github.Merges.Count);
        Assert.Equal(Swimlane.Done, SwimlaneOf(host, parked.Id));
        Assert.Equal(2, host.RoundsInFlight);
    }

    [Fact]
    public async Task A_reviewer_sending_a_work_item_back_is_not_blocked_by_a_full_ceiling()
    {
        // The rest of the asymmetry, and the one a reviewer is holding the response to. A
        // decline and a request for changes are decisions about a change somebody has
        // already read; neither starts a round, and neither should wait behind one
        // that has ninety minutes to run.
        var clock = new TestClock();
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode()
            .Producing("src/Index.cs +12 -3", "First attempt.")
            .Stuck()
            .Stuck();
        await using var host = await FactoryHost.StartAsync(root, clock, agent);

        var waiting = await Take(host, Nexus, 42, "nexus");
        await host.Settle();
        Assert.Equal(Swimlane.Review, SwimlaneOf(host, waiting.Id));

        await Take(host, Nexus, 43, "nexus");
        await Take(host, Nexus, 44, "nexus");
        await host.Settle();
        Assert.Equal(2, host.RoundsInFlight);

        using (await Board.DecideAsync(host.Board, waiting.Id, "request-changes", "Move the check inside the lock."))
        {
        }

        // Back into the build, and queued for a round rather than given one: the ceiling
        // is full, and a request for changes is not a licence to overspend it.
        Assert.Equal(Swimlane.Frontier, SwimlaneOf(host, waiting.Id));
        Assert.Equal(2, host.RoundsInFlight);
        Assert.Equal(2, agent.BeingHeld);

        // A decline is the same: a decision about a change that has already been read does
        // not wait for a round, and does not consume one.
        var other = await Take(host, Nexus, 45, "nexus");
        host.Store.Move(other.Id, Swimlane.Review);
        using (await Board.DecideAsync(host.Board, other.Id, "reject"))
        {
        }

        Assert.Equal(Swimlane.Rejected, SwimlaneOf(host, other.Id));
        Assert.Equal(2, host.RoundsInFlight);
    }

    [Fact]
    public async Task The_heartbeat_moves_the_machine_and_respects_the_ceiling_when_it_is_full()
    {
        // The driver, and the thing it has to be true of: a tick with a full ceiling starts
        // no round. A heartbeat that merely existed would be the failure #8 refused — a
        // timer with nothing behind it, spending a round on every issue of every
        // project at once — and the only way to know it is safe is to ask it to move a
        // machine that has nothing to move and check that it did not.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Stuck().Stuck().Stuck();
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        await Take(host, Nexus, 42, "nexus");
        await Take(host, Nexus, 43, "nexus");
        var third = await Take(host, Nexus, 44, "nexus");

        // One tick, with work to do and room in the ceiling. The tick settles the machine,
        // so it fills the ceiling rather than starting one round and stopping: that is what
        // a heartbeat is for, and the two that start here are two real rounds against two
        // real work items.
        await host.Tick();
        await host.PromoteBacklogAsync();

        Assert.Equal(2, agent.AskedFor.Count);
        Assert.Equal(2, agent.PeakConcurrency);
        Assert.Equal(2, host.RoundsInFlight);

        // The ceiling is full and there is a work item waiting for a round, and the
        // heartbeat is asked fifty times over. Every one of those ticks finds the ceiling
        // full and does nothing: no third round, and the work item still
        // waiting in Frontier rather than started.
        for (var tick = 0; tick < 50; tick++)
        {
            await host.Tick();
        }

        Assert.Equal(2, agent.AskedFor.Count);
        Assert.Equal(2, agent.PeakConcurrency);
        Assert.Equal(2, host.RoundsInFlight);
        Assert.Equal(Swimlane.Frontier, SwimlaneOf(host, third.Id));
        Assert.DoesNotContain(agent.AskedFor, round => round.WorkItemId == third.Id);

        // And when a slot does come free, the next tick spends it — so the fifty ticks
        // above were a machine that could not move rather than one that had stopped trying.
        agent.Release();
        await host.Tick();

        Assert.Equal(3, agent.AskedFor.Count);
        Assert.Contains(agent.AskedFor, round => round.WorkItemId == third.Id);
    }

    [Fact]
    public async Task The_heartbeat_steps_intake_as_well_as_the_loop()
    {
        // The other half of what the driver is for. A work item sitting in Backlog because
        // nothing had polled since the process started is exactly the gap every prior ticket
        // recorded, and a tick is what closes it: the poll interval is the poller's own
        // comparison against the clock, so a tick before it has passed is a no-op rather
        // than a read.
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var github = new FakeGitHub().WithRepository(
            Nexus,
            defaultBranch: "main",
            OpenIssue.Plain(1, "An issue nobody has looked at", IssueBody));

        // A round that comes straight back, because this is about the work item reaching
        // Review and not about where a round ends up. The first round is the routing
        // round the workflow now spends on every issue nobody has looked at, and it says
        // this one is already a build ticket (ADR-0015).
        var agent = new FakeNOpenCode()
            .RoutingAs(WorkItemKind.Build, "the issue names one endpoint and one test")
            .Producing("src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github);

        // One tick: the poll interval has not been waited for by anyone, and the first pass
        // is due the moment the factory comes up, so the issue is taken - into Backlog,
        // and then routed. It is still in Backlog afterwards, because a routed ticket is
        // still waiting for the reviewer's click that every Backlog work item waits for.
        await host.Tick();

        Assert.Equal([Nexus], github.Polled);
        var workItem = Assert.Single(host.Store.List());
        Assert.Equal(WorkItemKind.Build, workItem.Kind);
        Assert.Equal(Swimlane.Backlog, SwimlaneOf(host, workItem.Id));

        // One round was spent, and it was the routing one rather than a build — which is
        // the claim the whole routing layer makes about cost: an issue nobody has looked at
        // costs a round before it can be judged, and it does not build anything.
        Assert.Single(agent.AskedFor);
        Assert.Equal(RoundPurpose.Route, agent.AskedFor[0].Purpose);

        // The reviewer's acceptance, and the next tick builds what was accepted: intake,
        // routing and building are three acts now, and the tick drives all three.
        await host.PromoteAsync(workItem.Id);
        await host.Tick();
        Assert.Equal(Swimlane.Review, SwimlaneOf(host, workItem.Id));
        Assert.Equal(2, agent.AskedFor.Count);
        Assert.Equal(RoundPurpose.Build, agent.AskedFor[1].Purpose);

        // And a second tick before the poll interval has passed is a no-op rather than a
        // second read: the cadence is the poller's own comparison against the clock, not
        // something the driver imposes by ticking.
        host.Clock.Advance(TimeSpan.FromSeconds(59));
        await host.Tick();
        Assert.Equal(1, github.TimesPolled(Nexus));
    }

    [Fact]
    public void The_heartbeat_is_the_only_thing_in_the_process_that_waits_and_it_is_a_tick()
    {
        // Structural, and the reason the driver's one Task.Delay is not a hole in the
        // factory's "nothing waits" rule. What is forbidden is anything *inside* the policy:
        // the poller's interval, the round timeout, the retry backoffs and the feedback
        // threshold are all TimeSpans compared against IClock, and PolicyTests says so by
        // IL scan. The driver is the exception, named there and here. The ticket starter is
        // the third dependency and no new clock: whether it is asked anything at all is the
        // tick's own branching, compared against nothing that waits.
        Assert.Equal(TimeSpan.FromSeconds(5), FactoryConstants.HeartbeatInterval);
        Assert.Equal(
            ["ILogger`1", "ITicketStarter", "Orchestrator", "Poller"],
            typeof(FactoryDriver)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Select(parameter => parameter.ParameterType.Name)
                .Order(StringComparer.Ordinal)
                .ToList());
    }

    [Fact]
    public async Task Two_rounds_running_at_once_each_get_a_tree_of_their_own()
    {
        // The same claim end to end rather than by name: two rounds of two different
        // projects, run through the real round runner against the process seam, and two
        // working trees — each cloned by the runner's own commands into its own landing
        // directory. This is the one place the ceiling is not in play, and that is the
        // point: the ceiling bounds how many run at once, and ADR-0001's "nothing survives
        // a round" is unchanged by it.
        var root = Path.Combine(Path.GetTempPath(), "agent-factory-concurrency", Guid.NewGuid().ToString("n"));
        var source = LiftedTree.WithACommitOn();
        try
        {
            var processes = new FakeProcessCli().WithRealGit().AgentEdits("src/Index.cs", "changed\n", commit: true);

            var runner = new RoundRunner(
                processes,
                new ProjectLoadReport(
                    [
                        new Project("alpha", source.Path, "anthropic", "A_GITHUB", "A_LLM", "alpha.yaml"),
                        new Project("nexus", source.Path, "anthropic", "N_GITHUB", "N_LLM", "nexus.yaml"),
                    ],
                    []),
                new FactoryOptions("factories", Path.Combine(root, "agent-factory.db"), new Uri("http://127.0.0.1:0")),
                new RoundResultDeriver(NullLogger<RoundResultDeriver>.Instance),
                new FakeCredentialReader(),
                NullLogger<RoundRunner>.Instance);

            var alpha = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
            var nexus = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

            // Both at once. The fake process seam is called from two threads, which is what
            // the machine does with two real rounds, and each round's agent ran in a
            // working directory of its own. Both rounds clone the same local source, which
            // is what two projects on one machine looks like.
            var options = new FactoryOptions("factories", Path.Combine(root, "agent-factory.db"), new Uri("http://127.0.0.1:0"));
            var alphaRound = ARound(alpha, "alpha", source.Path);
            var nexusRound = ARound(nexus, "nexus", source.Path);
            var alphaTask = runner.RunRoundAsync(alphaRound, CancellationToken.None);
            var nexusTask = runner.RunRoundAsync(nexusRound, CancellationToken.None);
            var landings = new[]
            {
                RoundRunner.LandingFor(options, alphaRound),
                RoundRunner.LandingFor(options, nexusRound),
            };
            await Task.WhenAll(alphaTask, nexusTask).WaitAsync(TimeSpan.FromSeconds(120));

            // Both rounds produced, and neither was refused at the clone: a round that
            // failed here would silently pass the count assertions below if the results
            // were not read. The results are awaited rather than read off `.Result`, which
            // would block the test thread and turn a hang into a deadlock.
            var alphaLanded = await alphaTask;
            var nexusLanded = await nexusTask;
            Assert.Equal([RoundOutcome.Produced, RoundOutcome.Produced], [alphaLanded.Outcome, nexusLanded.Outcome]);

            Assert.Equal(2, processes.From("opencode").Count);
            Assert.Equal(2, landings.Distinct(StringComparer.Ordinal).Count());
            Assert.All(landings, landing =>
                Assert.True(Directory.Exists(Path.Combine(landing, "tree", ".git")), $"{landing} has no tree of its own"));

            // And each round used its own project's environment and nothing of the other's
            // — the brief handed to one agent is the other's nowhere.
            Assert.Equal(
                2,
                processes.From("opencode")
                    .Select(call => call.Environment["AGENT_FACTORY_AGENT_PROMPT"])
                    .Distinct(StringComparer.Ordinal)
                    .Count());
        }
        finally
        {
            source.Dispose();

            // A round's files are kept rather than cleaned up (ADR-0006), so the test
            // removes its own throwaway directory rather than leaving the next run one it
            // did not create.
            try
            {
                GitTrees.Delete(root);
            }
            catch (IOException)
            {
                // A leftover temporary directory is not worth failing a test over.
            }
        }
    }

    /// <summary>A round, as the loop would hand one to the agent.</summary>
    private static Round ARound(Guid workItemId, string project, string? repoUrl = null) => new(
        workItemId,
        project,
        repoUrl ?? (project == "alpha" ? "https://github.com/NaniSoft/alpha" : Nexus),
        42,
        "An issue",
        IssueBody,
        "main",
        string.Empty,
        1,
        1);

    private static async Task<WorkItem> Take(FactoryHost host, string repoUrl, int issueNumber, string project)
    {
        var workItem = host.Store
            .Intake(project, repoUrl, issueNumber, $"Issue {issueNumber}", IssueBody, "main")
            .WorkItem;
        await host.PromoteAsync(workItem.Id);
        return workItem;
    }

    private static Swimlane SwimlaneOf(FactoryHost host, Guid id) => host.Store.Get(id)!.Swimlane;
}
