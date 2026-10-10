namespace AgentFactory.Tests.Intake;

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
