namespace AgentFactory.Tests.Rounds;

using System.Xml.Linq;
using AgentFactory.GitHub;
using AgentFactory.Loop;
using AgentFactory.Rounds;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;

/// <summary>
/// A round, with the agent faked. The loop is real, the store is real, the board is
/// real; only the agent and the clock are substituted. The spine of the loop is
/// provable here with no container and no network.
/// </summary>
public class RoundTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";

    private const string IssueBody = "What the issue says, in the maintainer's words.";

    [Fact]
    public async Task A_work_item_moves_backlog_frontier_in_progress_and_review()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main").WorkItem;

        Assert.Equal(Swimlane.Backlog, SwimlaneOf(host, workItem.Id));

        // One step, one transition, asserted as it happens rather than slept through.
        Assert.True(await host.Step());
        Assert.Equal(Swimlane.Frontier, SwimlaneOf(host, workItem.Id));

        Assert.True(await host.Step());
        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, workItem.Id));

        Assert.True(await host.Step());
        Assert.Equal(Swimlane.Review, SwimlaneOf(host, workItem.Id));
    }

    [Fact]
    public async Task A_work_item_waits_in_frontier_for_a_container_and_is_built_when_one_frees()
    {
        // **This test changed shape, deliberately, for the container budget.** It used to
        // take two work items and assert the second stayed in Backlog, which is the #3
        // shape: one slot, and a slot was a boolean. The budget makes that false by
        // construction — with a budget of two, two work items both get containers, and a
        // test that kept asserting "the second is in Backlog" would be asserting that the
        // budget is one. It takes three now, and asserts the thing the budget is for: the
        // third waits, it waits in Frontier rather than in Backlog, and it starts as soon
        // as a container comes back rather than being refused.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Stuck().Stuck().Stuck();
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var first = host.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main").WorkItem;
        var second = host.Store.Intake("nexus", RepoUrl, 43, "A round, with the agent faked", IssueBody, "main").WorkItem;
        var third = host.Store.Intake("nexus", RepoUrl, 44, "Waiting for a container", IssueBody, "main").WorkItem;

        // The budget fills, and the third work item is accepted and queued — not held back
        // in Backlog as though nothing had been accepted, and not started without a
        // container.
        await host.Settle();

        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, first.Id));
        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, second.Id));
        Assert.Equal(Swimlane.Frontier, SwimlaneOf(host, third.Id));
        Assert.Equal(2, agent.AskedFor.Count);
        Assert.Equal(2, host.RoundsInFlight);

        // A container comes back, and the next step gives it to the work item that has been
        // waiting. This is the ordering the whole budget exists for: a long build holds one
        // of the two, and the queue behind it moves.
        agent.Release();
        await host.Settle();

        Assert.Equal(Swimlane.Review, SwimlaneOf(host, first.Id));
        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, third.Id));
        Assert.Equal(3, agent.AskedFor.Count);
    }

    [Fact]
    public async Task One_call_to_the_agent_is_one_round_and_one_result()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main").WorkItem;

        await host.Settle();

        var round = Assert.Single(agent.AskedFor);
        Assert.Equal(workItem.Id, round.WorkItemId);
        Assert.Equal("nexus", round.Project);
        Assert.Equal(RepoUrl, round.RepoUrl);
        Assert.Equal(42, round.IssueNumber);
        Assert.Equal("main", round.BaseBranch);

        var recorded = Assert.Single(host.Store.Rounds(workItem.Id));
        Assert.Equal(1, recorded.RoundNumber);
        Assert.Equal(RoundOutcome.Produced, recorded.Outcome);
        Assert.Equal("src/Index.cs +12 -3", recorded.ResultPayload);
        Assert.Equal("Added the endpoint.", recorded.AgentNote);
        Assert.Equal(1, host.Store.Get(workItem.Id)!.RoundCount);
    }

    [Fact]
    public async Task A_work_item_keeps_every_rounds_result_and_not_only_the_latest()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode()
            .Yielding(RoundOutcome.Produced, "round one result", "first note")
            .Yielding(RoundOutcome.Produced, "round two result", "second note")
            .Yielding(RoundOutcome.Produced, "round three result", "third note");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main").WorkItem;

        // Three rounds, each one started by a reviewer asking for changes on the board:
        // the decision is what puts the work item back in the build, so this is the same
        // sequence a reviewer produces rather than a store call made for the test.
        await host.Settle();
        using (await Board.DecideAsync(host.Board, workItem.Id, "request-changes", "the first thing to change"))
        {
        }

        await host.Settle();
        using (await Board.DecideAsync(host.Board, workItem.Id, "request-changes", "and then the second"))
        {
        }

        await host.Settle();

        var rounds = host.Store.Rounds(workItem.Id);
        Assert.Equal([1, 2, 3], rounds.Select(round => round.RoundNumber));
        Assert.Equal(
            ["round one result", "round two result", "round three result"],
            rounds.Select(round => round.ResultPayload));
        Assert.Equal(["first note", "second note", "third note"], rounds.Select(round => round.AgentNote));
        Assert.Equal(3, rounds.Count);
        Assert.Equal(3, host.Store.Get(workItem.Id)!.RoundCount);
    }

    [Fact]
    public async Task A_round_that_runs_past_the_round_timeout_ends_and_escalates()
    {
        var clock = new TestClock();
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Stuck();
        await using var host = await FactoryHost.StartAsync(root, clock: clock, agent: agent);
        var workItem = host.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main").WorkItem;

        await host.Settle();
        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, workItem.Id));

        // A round is bounded at ninety minutes, and the number is written out here rather
        // than read from the constant: a test that advances by the constant proves only
        // that the machine compares two values, not that the bound is the one we claim.
        Assert.Equal(TimeSpan.FromMinutes(90), FactoryConstants.RoundTimeout);

        // Short of the timeout, the round is still the round.
        clock.Advance(TimeSpan.FromMinutes(89));
        await host.Settle();
        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, workItem.Id));
        Assert.Empty(host.Store.Rounds(workItem.Id));

        // Past it, the round is over and nothing is merged over the objection.
        clock.Advance(TimeSpan.FromMinutes(1));
        await host.Settle();

        Assert.Equal(Swimlane.Escalated, SwimlaneOf(host, workItem.Id));
        var recorded = Assert.Single(host.Store.Rounds(workItem.Id));
        Assert.Equal(RoundOutcome.TimedOut, recorded.Outcome);
        Assert.Equal(1, host.Store.Get(workItem.Id)!.RoundCount);
        Assert.True(agent.EndedARound, "the factory ends the round it stopped waiting for");
    }

    [Fact]
    public async Task A_work_items_rounds_survive_a_restart()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "Added the endpoint.");

        Guid workItemId;
        await using (var first = await FactoryHost.StartAsync(root, agent: agent))
        {
            var workItem = first.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main").WorkItem;
            workItemId = workItem.Id;
            await first.Settle();
        }

        await using var restarted = await FactoryHost.StartAsync(root);

        var round = Assert.Single(restarted.Store.Rounds(workItemId));
        Assert.Equal(RoundOutcome.Produced, round.Outcome);
        Assert.Equal("src/Index.cs +12 -3", round.ResultPayload);
        Assert.Equal(Swimlane.Review, SwimlaneOf(restarted, workItemId));
    }

    [Fact]
    public async Task The_board_shows_the_round_count_and_the_result_for_a_work_item_in_review()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main").WorkItem;

        await host.Settle();

        var board = await Board.ReadAsync(host.Board);

        // The work item is in Review, and Review is what carries its rounds.
        Assert.Contains(workItem.Id.ToString(), board.Swimlane("Review"), StringComparison.Ordinal);
        Assert.Equal("1", board.Rendered("data-work-item", workItem.Id.ToString(), "data-round-count"));

        var review = board.Read("Review");
        Assert.Contains("round 1 of 3", review, StringComparison.Ordinal);
        Assert.Contains("src/Index.cs +12 -3", review, StringComparison.Ordinal);
        Assert.Contains("Added the endpoint.", review, StringComparison.Ordinal);
        Assert.Contains("Produced", review, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_round_that_came_back_without_a_result_is_recorded_and_escalated()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Throwing("the container died mid-round");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main").WorkItem;

        await host.Settle();

        // A failure is visible rather than silent, and it is never merged over. Whether
        // it is worth another attempt is the retry ticket's classification, so what this
        // asserts is the part that belongs here: the round was recorded, and the work item
        // parked where a human can still finish it (ADR-0008).
        Assert.Equal(Swimlane.Escalated, SwimlaneOf(host, workItem.Id));
        Assert.Equal(RoundOutcome.Failed, Assert.Single(host.Store.Rounds(workItem.Id)).Outcome);
        Assert.Equal(1, host.Store.Get(workItem.Id)!.RoundCount);
    }

    [Fact]
    public async Task A_round_cannot_be_recorded_against_a_work_item_that_is_not_there()
    {
        using var root = FactoryRoot.Create();
        await using var host = await FactoryHost.StartAsync(root);

        // Round results travel with the work item, so an orphan round is a record nothing
        // can read. The store refuses it rather than keeping a row the board will never
        // render.
        Assert.Throws<KeyNotFoundException>(() => host.Store.RecordRound(
            Guid.NewGuid(),
            RoundOutcome.Produced,
            "a result for nothing",
            null,
            host.Clock.UtcNow));

        Assert.Throws<KeyNotFoundException>(
            () => host.Store.Move(Guid.NewGuid(), Swimlane.Review));
    }

    [Fact]
    public void The_orchestrator_references_no_container_runtime()
    {
        // A structural check, and the only kind available for a claim about what code does
        // not reference. The whole of the orchestrator's knowledge of the outside world is
        // its constructor: the store, the two seams — one call is one round, one call is
        // one merge — the clock, a logger, and the counters. A container runtime would have
        // to arrive as an extra dependency, as a package reference, or as an assembly
        // reference, so all three are checked. It would not catch a runtime reached by
        // shelling out, which is the gap a reader should know about.
        //
        // The sixth dependency is `FactoryMetrics` and it is not a sixth boundary: it holds
        // no clock, no transport, no credential reader, no store, no seam and no options,
        // and it counts over an in-process API. The seventh, `FactoryOptions`, is the
        // auto-merge switch (#36) — settings, not a seam: nothing to call, nothing behind
        // it. `PolicyTests` asserts that list by hand and names each change as it was made,
        // so a reader is not left wondering what arrived and why.
        Assert.Equal(
            ["IWorkItemStore", "INOpenCode", "IGitHub", "IClock", "ILogger`1", "FactoryMetrics", "FactoryOptions"],
            typeof(Orchestrator)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Select(parameter => parameter.ParameterType.Name));

        // A round's result is the only thing the machine learns from the round, so a second
        // way to learn one would be a second seam by another name. One call in the seam
        // interface, one call on the object.
        var calls = typeof(INOpenCode).GetMethods().Select(method => method.Name).ToList();
        Assert.Equal(["RunRoundAsync"], calls);

        // And the machine's knowledge of merging is one call too, through the same one seam
        // intake reads through. Done means merged, so this call is what an approval has to
        // go through; a loop that decided for itself that a change was shipped would need
        // no seam at all, and that is the defect this rule exists to keep fixed.
        var merges = typeof(IGitHub).GetMethods()
            .Where(method => method.Name.StartsWith("Merge", StringComparison.Ordinal))
            .Select(method => method.Name)
            .ToList();
        Assert.Equal(["MergeAsync"], merges);

        var project = FactoryProjectFile();
        var packages = project.Descendants("PackageReference")
            .Select(reference => (string?)reference.Attribute("Include") ?? string.Empty);

        Assert.DoesNotContain(
            packages,
            package => package.Contains("Docker", StringComparison.OrdinalIgnoreCase)
                || package.Contains("Podman", StringComparison.OrdinalIgnoreCase)
                || package.Contains("Testcontainers", StringComparison.OrdinalIgnoreCase));

        var assemblies = typeof(Orchestrator).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty);

        Assert.DoesNotContain(
            assemblies,
            assembly => assembly.Contains("Docker", StringComparison.OrdinalIgnoreCase)
                || assembly.Contains("Podman", StringComparison.OrdinalIgnoreCase));
    }

    private static XDocument FactoryProjectFile()
    {
        // Walk up to the solution rather than counting directories: the path from the
        // test binaries to the factory's project file is a build-output detail.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "src", "agent-factory", "agent-factory.csproj");
            if (File.Exists(path))
            {
                using var project = File.OpenRead(path);
                return XDocument.Load(project);
            }
        }

        throw new DirectoryNotFoundException(
            $"no agent-factory.csproj found above {AppContext.BaseDirectory}");
    }

    private static Swimlane SwimlaneOf(FactoryHost host, Guid id) => host.Store.Get(id)!.Swimlane;
}
