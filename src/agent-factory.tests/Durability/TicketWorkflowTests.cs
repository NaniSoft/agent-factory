namespace AgentFactory.Tests.Durability;

using AgentFactory.Durability;
using AgentFactory.Domain.WorkItems;
using AgentFactory.GitHub;
using AgentFactory.Routing;
using AgentFactory.Domain.Rounds;
using AgentFactory.Domain.Routing;
using AgentFactory.Domain.Issues;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Client;
using Temporalio.Worker;

/// <summary>
/// The per-ticket workflow, run for real against the Temporal server the deployment runs
/// (the compose dev server on loopback 7233): the same workflow, the same activities, the
/// same policy production runs — with the fakes standing in for the world exactly as they
/// do for the loop's own tests.
/// </summary>
/// <remarks>
/// What is asserted is the workflow's control flow: a ticket is routed, waits for
/// acceptance, is built once accepted, lands in Review, and merges when the reviewer
/// approves; and a grilled ticket waits for its answers in the questions issue's
/// conversation, hands back when they arrive, and routes again with the conversation in
/// its brief. The waits are real time, because the dev server has no clock to skip: a
/// round is a fake agent call (fast), and the answer poll is five seconds, so both tests
/// settle in well under a minute.
/// </remarks>
public class TicketWorkflowTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";

    private static async Task<ITemporalClient> ClientAsync()
    {
        return await TemporalClient.ConnectAsync(new()
        {
            TargetHost = "localhost:7233",
            Namespace = "default",
        });
    }

    [TemporalFact]
    public async Task A_build_ticket_is_routed_accepted_built_and_merged()
    {
        var client = await ClientAsync();
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var store = new SqliteWorkItemStore(root.DatabasePath, new TestClock());
        var github = new FakeGitHub()
            .WithRepository(
            RepoUrl, "main", OpenIssue.Plain(42, "the issue being built", "What the issue says."))
            .Merging();
        var agent = new FakeNOpenCode()
            .RoutingAs(WorkItemKind.Build, "the issue names the change and how to judge it")
            .Producing("src/Index.cs +12 -3", "Built the change.");
        var activities = new TicketActivities(
            store,
            agent,
            new IssuePublisher(github, NullLogger<IssuePublisher>.Instance),
            github,
            NullLogger<TicketActivities>.Instance);

        var workItem = store.Intake(
            "nexus", RepoUrl, 42, "the issue being built", "What the issue says.", "main").WorkItem;
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

        var handle = client.GetWorkflowHandle<TicketWorkflow>(workflowId);
        await handle.ExecuteUpdateAsync(wf => wf.Accept());

        // The decision waits for the build to land: a decision about a ticket that is not
        // in Review is refused by the workflow itself, and a test that sent one anyway
        // would be waiting for an answer nobody was ever going to give.
        await WaitUntilInReviewAsync(store, workItem.Id);

        var approved = await DecideWhenAcceptedAsync(handle);

        await handle.GetResultAsync();

        Assert.Equal(Swimlane.Done, store.Get(workItem.Id)!.Swimlane);

        // One routing round and one build round: the route is not charged, the build is,
        // and neither ran twice for one ticket.
        Assert.Equal(2, agent.AskedFor.Count);
        Assert.Equal(
            [RoundPurpose.Route.ToString(), RoundPurpose.Build.ToString()],
            agent.AskedFor.Select(round => round.Purpose.ToString()));

        var merge = Assert.Single(github.Merges);
        Assert.Equal(RepoUrl, merge.RepoUrl);
        Assert.Equal(42, merge.IssueNumber);

        await stopped.CancelAsync();
        // An abrupt cancel is the worker being told to stop, and the worker says so:
        // awaiting it as a result would make the shutdown itself a failure.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [TemporalFact]
    public async Task A_grilled_ticket_waits_for_its_answer_hands_back_and_routes_again()
    {
        var client = await ClientAsync();
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var store = new SqliteWorkItemStore(root.DatabasePath, new TestClock());
        var github = new FakeGitHub()
            .WithRepository(
            RepoUrl, "main", OpenIssue.Plain(42, "the issue being grilled", "What the issue says."))
            .Merging();
        var agent = new FakeNOpenCode()
            .RoutingAs(
                WorkItemKind.Grill,
                "the shape is clear and two decisions about it are not",
                [new ProposedQuestion("does a draft count as published?", "it changes what a click means")])
            .RoutingAs(WorkItemKind.Build, "the answers in the conversation settle both decisions")
            .Producing("src/Hello.cs +8 -1", "Built against the answers.");
        var activities = new TicketActivities(
            store,
            agent,
            new IssuePublisher(github, NullLogger<IssuePublisher>.Instance),
            github,
            NullLogger<TicketActivities>.Instance);

        var workItem = store.Intake(
            "nexus", RepoUrl, 42, "the issue being grilled", "What the issue says.", "main").WorkItem;
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

        // Wait until the Grill has landed: the questions issue is filed, and the ticket
        // waits on its answers in the Waiting lane.
        var question = await WaitForAsync(() =>
        {
            var item = store.Get(workItem.Id)!;
            return item.RoutingIssue;
        });

        Assert.Equal(Swimlane.Waiting, store.Get(workItem.Id)!.Swimlane);

        // The human answers in the conversation. The workflow's durable wait hears it, and
        // the next activity poll hands the ticket back to routing.
        github.WithComments(
            RepoUrl,
            question!.Value,
            new IssueComment(
                "dpven",
                "drafts count as published, and the schema stays as it is.",
                DateTimeOffset.UtcNow.AddMinutes(1)));

        // The acceptance gate, and then the build the gate admits — which the reviewer's
        // decision waits behind, for the same reason the golden path does.
        var handle = client.GetWorkflowHandle<TicketWorkflow>(workflowId);
        await handle.ExecuteUpdateAsync(wf => wf.Accept());
        await WaitUntilInReviewAsync(store, workItem.Id);

        var approved = await DecideWhenAcceptedAsync(handle);

        await handle.GetResultAsync();
        Assert.Equal(Swimlane.Done, store.Get(workItem.Id)!.Swimlane);

        // Three rounds on the record: the Grill, the re-route after the answer, and the
        // build — of which one was charged to the reviewer's ceiling.
        Assert.Equal(3, agent.AskedFor.Count);

        // And the second routing round carried the human's answer in its conversation,
        // which is the whole reason the hand-back exists.
        var reroute = agent.AskedFor[1];
        var said = Assert.Single(reroute.Conversation);
        Assert.Equal("dpven", said.Author);
        Assert.Contains("drafts count as published", said.Body, StringComparison.Ordinal);

        await stopped.CancelAsync();
        // An abrupt cancel is the worker being told to stop, and the worker says so:
        // awaiting it as a result would make the shutdown itself a failure.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }


    /// <summary>
    /// Waits until the ticket's build round has landed, which is the moment a reviewer's
    /// decision means something: the workflow refuses a decision about a ticket that is
    /// not in Review, and a decision sent before the build finished would come back as a
    /// refusal the test had not read — leaving the workflow waiting for a decision nobody
    /// was still holding.
    /// </summary>
    private static async Task WaitUntilInReviewAsync(SqliteWorkItemStore store, Guid workItemId, int attempts = 240)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (store.Get(workItemId) is { Swimlane: Swimlane.Review })
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new Xunit.Sdk.XunitException(
            $"the ticket never reached Review after {attempts * 250.0 / 1000:0}s, so there is nothing to decide");
    }

    /// <summary>
    /// Decides, and retries while the workflow is still catching up with what its own
    /// activity did. The store shows Review the instant the build activity moves the
    /// ticket; the workflow's own <c>_inReview</c> flips one beat later, when the
    /// activity's result has been processed — and a decision in that beat is refused, the
    /// same answer a reviewer's too-early click gets. A second click is what a human
    /// would give, so that is what the test gives.
    /// </summary>
    private static async Task<TicketDecisionResult> DecideWhenAcceptedAsync(
        Temporalio.Client.WorkflowHandle<TicketWorkflow> handle, int attempts = 40)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var answer = await handle.ExecuteUpdateAsync(
                wf => wf.DecideAsync(new TicketDecision("approve", string.Empty)));

            if (answer.Applied)
            {
                return answer;
            }

            await Task.Delay(250);
        }

        throw new Xunit.Sdk.XunitException("the decision was never applied; the workflow kept refusing it");
    }

    private static async Task<int?> WaitForAsync(Func<int?> condition, int attempts = 120)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (condition() is { } value)
            {
                return value;
            }

            await Task.Delay(250);
        }

        return null;
    }
}
