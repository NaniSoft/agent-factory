namespace AgentFactory.Tests.Rounds;

using System.Xml.Linq;
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
        Assert.True(host.Step());
        Assert.Equal(Swimlane.Frontier, SwimlaneOf(host, workItem.Id));

        Assert.True(host.Step());
        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, workItem.Id));

        Assert.True(host.Step());
        Assert.Equal(Swimlane.Review, SwimlaneOf(host, workItem.Id));
    }

    [Fact]
    public async Task A_work_item_is_accepted_into_frontier_when_a_slot_frees()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Stuck().Stuck();
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var first = host.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main").WorkItem;
        var second = host.Store.Intake("nexus", RepoUrl, 43, "A round, with the agent faked", IssueBody, "main").WorkItem;

        // The first work item takes the only slot and stays in it while its round runs.
        host.Settle();
        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, first.Id));
        Assert.Equal(Swimlane.Backlog, SwimlaneOf(host, second.Id));
        Assert.Single(agent.AskedFor);

        // The round returns, so the slot frees, so the second is accepted.
        agent.Release();
        host.Settle();

        Assert.Equal(Swimlane.Review, SwimlaneOf(host, first.Id));
        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, second.Id));
    }

    [Fact]
    public async Task One_call_to_the_agent_is_one_round_and_one_result()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main").WorkItem;

        host.Settle();

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
        host.Settle();
        using (await Board.DecideAsync(host.Board, workItem.Id, "request-changes", "the first thing to change"))
        {
        }

        host.Settle();
        using (await Board.DecideAsync(host.Board, workItem.Id, "request-changes", "and then the second"))
        {
        }

        host.Settle();

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

        host.Settle();
        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, workItem.Id));

        // A round is bounded at ninety minutes, and the number is written out here rather
        // than read from the constant: a test that advances by the constant proves only
        // that the machine compares two values, not that the bound is the one we claim.
        Assert.Equal(TimeSpan.FromMinutes(90), FactoryConstants.RoundTimeout);

        // Short of the timeout, the round is still the round.
        clock.Advance(TimeSpan.FromMinutes(89));
        host.Settle();
        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, workItem.Id));
        Assert.Empty(host.Store.Rounds(workItem.Id));

        // Past it, the round is over and nothing is merged over the objection.
        clock.Advance(TimeSpan.FromMinutes(1));
        host.Settle();

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
            first.Settle();
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

        host.Settle();

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

        host.Settle();

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
        // its constructor: the store, the agent, the clock, a logger. A container runtime
        // would have to arrive as a fifth dependency, as a package reference, or as an
        // assembly reference, so all three are checked. It would not catch a runtime
        // reached by shelling out, which is the gap a reader should know about.
        Assert.Equal(
            ["IWorkItemStore", "INOpenCode", "IClock", "ILogger`1"],
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
