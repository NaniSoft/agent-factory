namespace AgentFactory.Tests.Intake;

using System.Net;
using AgentFactory.Loop;
using AgentFactory.Rounds;
using AgentFactory.Tests.Boundary;
using System.Net.Http;
using AgentFactory.WorkItems;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The gate between Backlog and Frontier (#40): intake is indiscriminate, and the board
/// is where a human decides what is worth building — which is only true if nothing is
/// built until a reviewer says so.
/// </summary>
public class BacklogGateTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";

    private const string IssueBody = "What the issue says, in the maintainer's words.";

    [Fact]
    public async Task A_work_item_in_backlog_is_not_built_until_a_reviewer_accepts_it()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main")
            .WorkItem;

        // Whatever the machine does on its own — the poller's turn, the loop's step, the
        // heartbeat's tick — a work item nobody has accepted stays where intake put it,
        // and no container is spent on it.
        await host.Settle();
        await host.Tick();

        Assert.Equal(Swimlane.Backlog, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Empty(agent.AskedFor);
    }

    [Fact]
    public async Task A_reviewers_acceptance_moves_a_backlog_item_into_the_build()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main")
            .WorkItem;

        var promoted = await host.Services.GetRequiredService<Orchestrator>()
            .PromoteAsync(workItem.Id);
        Assert.True(promoted);
        Assert.Equal(Swimlane.Frontier, host.Store.Get(workItem.Id)!.Swimlane);

        // And acceptance is what puts it in the build: the loop picks Frontier up, the
        // same as it always did — the gate is the only thing that changed.
        await host.Settle();
        Assert.Equal(Swimlane.Review, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Single(agent.AskedFor);
    }

    [Fact]
    public async Task The_board_offers_the_acceptance_and_the_loop_applies_it()
    {
        // The gate, end to end at the surface: a Backlog card carries the build control, and
        // the click goes through the endpoint to the orchestrator — the same component that
        // applies every other transition — and the item moves. The button reaches the app's
        // Board as `data-build`, gated on the lane the way the old board gated it; the post
        // is the one the app's own "Accept into build" control makes.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main")
            .WorkItem;

        var board = await Board.ReadAsync(host.Board);
        Assert.Contains($"data-build=\"{workItem.Id}\"", board.Html, StringComparison.Ordinal);

        using var response = await Board.AcceptAsync(host.Board, workItem.Id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var answer = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"applied\":true", answer, StringComparison.Ordinal);
        Assert.Equal(Swimlane.Frontier, host.Store.Get(workItem.Id)!.Swimlane);

        // And acceptance is what puts it in the build: the loop picks Frontier up, the same
        // as it always did — the gate is the only thing that changed.
        await host.Settle();
        Assert.Equal(Swimlane.Review, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Single(agent.AskedFor);
    }

    [Fact]
    public async Task The_acceptance_is_refused_when_the_work_item_is_not_waiting_in_backlog()
    {
        // The refusal on the surface the app reads: an item already past Backlog is not
        // accepted again, and the endpoint says so in its own words rather than moving it.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main")
            .WorkItem;
        host.Store.Move(workItem.Id, Swimlane.Review);

        using var response = await Board.AcceptAsync(host.Board, workItem.Id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var answer = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"applied\":false", answer, StringComparison.Ordinal);
        Assert.Contains("not waiting in Backlog", answer, StringComparison.Ordinal);
        Assert.Equal(Swimlane.Review, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Empty(agent.AskedFor);
    }

    [Fact]
    public async Task Acceptance_is_refused_for_a_work_item_not_waiting_in_backlog()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main")
            .WorkItem;
        host.Store.Move(workItem.Id, Swimlane.Review);

        var promoted = await host.Services.GetRequiredService<Orchestrator>()
            .PromoteAsync(workItem.Id);

        Assert.False(promoted);
        Assert.Equal(Swimlane.Review, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Empty(agent.AskedFor);
    }
}
