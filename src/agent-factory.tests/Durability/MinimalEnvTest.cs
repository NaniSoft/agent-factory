namespace AgentFactory.Tests.Durability;


using AgentFactory.Durability;
using AgentFactory.Domain.WorkItems;
using AgentFactory.GitHub;
using AgentFactory.Routing;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Client;
using Temporalio.Worker;
using Xunit;

public class MinimalEnvTest
{
    [Fact]
    public async Task Route_only_run_survives()
    {
        var client = await TemporalClient.ConnectAsync(new() { TargetHost = "localhost:7233", Namespace = "default" });
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var store = new SqliteWorkItemStore(root.DatabasePath, new TestClock());
        var github = new FakeGitHub().WithRepository(
            "https://github.com/NaniSoft/nexus", "main", OpenIssue.Plain(42, "t", "b"));
        var agent = new FakeNOpenCode().RoutingAs(WorkItemKind.Build, "names the change");
        var activities = new TicketActivities(
            store,
            agent,
            new IssuePublisher(github, NullLogger<IssuePublisher>.Instance),
            github,
            NullLogger<TicketActivities>.Instance);

        var workItem = store.Intake("nexus", "https://github.com/NaniSoft/nexus", 42, "t", "b", "main").WorkItem;
        var workflowId = $"ticket-{workItem.Id:N}-{Guid.NewGuid():N}";

        using var stopped = new CancellationTokenSource();
        using var worker = new TemporalWorker(
            client,
            new TemporalWorkerOptions(TemporalTickets.TaskQueue + "-" + Guid.NewGuid().ToString("N")) { MaxConcurrentActivities = 2 }
                .AddWorkflow<TicketWorkflow>()
                .AddAllActivities<ITicketActivities>(activities));
        var running = worker.ExecuteAsync(stopped.Token);

        await client.StartWorkflowAsync(
            (TicketWorkflow wf) => wf.RunAsync(new TicketInput(workItem.Id, AutoMerge: false)),
            new(id: workflowId, taskQueue: worker.Options.TaskQueue!));

        // Wait for the route to land: Backlog, Build.
        var landed = false;
        for (var i = 0; i < 120 && !landed; i++)
        {
            landed = store.Get(workItem.Id) is { } item
                && item.Swimlane == Swimlane.Backlog
                && item.Kind == WorkItemKind.Build;
            if (!landed) await Task.Delay(250);
        }

        Assert.True(landed, "the route never landed");
        Assert.Single(agent.AskedFor);

        await stopped.CancelAsync();
        try { await running; } catch (OperationCanceledException) { }
    }
}
