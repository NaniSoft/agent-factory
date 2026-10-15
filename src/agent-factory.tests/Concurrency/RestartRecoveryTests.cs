namespace AgentFactory.Tests.Concurrency;

using AgentFactory.GitHub;
using AgentFactory.Routing;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;

/// <summary>
/// What a restart does to a round that was in flight when the process died.
/// </summary>
/// <remarks>
/// The pending-round row is written before the ask and read at startup, and this test
/// walks the whole life of it: a round asked and held open (the process dies here), a
/// restart over the same store, and the recovery — interrupted container taken away, row
/// cleared, work item re-queued in Frontier, and the round asked for again rather than the
/// ticket being stranded in In Progress for a landing that died with the old process.
/// </remarks>
public class RestartRecoveryTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";

    [Fact]
    public async Task A_restart_mid_round_asks_the_round_again_rather_than_stranding_the_ticket()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var deadProcess = new FakeNOpenCode().Stuck();
        var first = await FactoryHost.StartAsync(root, agent: deadProcess);

        first.GitHub.WithRepository(RepoUrl, "main", OpenIssue.Plain(42, "the issue being built", "What the issue says."));
        await first.PollAsync();
        var workItem = Assert.Single(first.Store.List());
        first.Route(workItem.Id, AgentFactory.WorkItems.WorkItemKind.Build);
        await first.PromoteBacklogAsync();
        await first.Step();

        // In flight, and on the record: the row exists while the round runs, which is what
        // a hard death leaves behind for the next process to read.
        Assert.Equal(Swimlane.InProgress, first.Store.Get(workItem.Id)!.Swimlane);
        Assert.Single(first.Store.ListPendingRounds());

        // The process dies here — mid-round, no result, no landing. The row survives it,
        // because it was written before the ask and only a landing clears it.
        await first.DisposeAsync();

        // The restart: a new process over the same store. Recovery runs at startup, before
        // the heartbeat, and the ticket is not stranded.
        var restartedAgent = new FakeNOpenCode().Producing("src/Index.cs +12 -3", "Built it.");
        await using var second = await FactoryHost.StartAsync(root, agent: restartedAgent);

        Assert.Empty(second.Store.ListPendingRounds());
        Assert.Equal(Swimlane.Frontier, second.Store.Get(workItem.Id)!.Swimlane);

        // And the factory finishes what the dead process started: the re-asked round lands,
        // and the ticket reaches Review as if nothing had died — because nothing of the
        // reviewer's budget was spent on the factory's own restart.
        await second.Settle();

        var landed = second.Store.Get(workItem.Id)!;
        Assert.Equal(Swimlane.Review, landed.Swimlane);
        Assert.Equal(WorkItemKind.Build, landed.Kind);
    }
}
