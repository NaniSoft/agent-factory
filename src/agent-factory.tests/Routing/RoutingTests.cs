namespace AgentFactory.Tests.Routing;

using AgentFactory.Application.Observability;
using AgentFactory.Application;
using AgentFactory.Domain.Failures;
using AgentFactory.Domain.WorkItems;
using AgentFactory.Application.GitHub;
using AgentFactory.GitHub;
using AgentFactory.Domain.Rounds;
using AgentFactory.Domain.Routing;
using AgentFactory.Domain.Issues;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;

/// <summary>
/// The routing layer end to end through the real loop, the real store and the real board's
/// own gate (ADR-0015).
/// </summary>
/// <remarks>
/// <para>
/// The claim under test is a chain of three refusals and one file, and the tests are in that
/// order because each is the reason the next one exists:
///
/// <list type="number">
/// <item>a ticket that can be built is routed and then waits in Backlog for the reviewer;</item>
/// <item>a ticket that cannot be built has its questions *published* before it is recorded as
/// needing them, because a work item waiting on a question nobody was asked is the failure
/// this whole design is against;</item>
/// <item>a ticket the factory cannot file its questions for is <em>parked</em> rather than
/// recorded as needing an answer;</item>
/// <item>and a verdict nobody can read is parked, never guessed.</item>
/// </list>
/// </para>
/// </remarks>
public class RoutingTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";

    private const string Body = "What the maintainer wrote.";

    [Fact]
    public async Task An_issue_routed_as_a_build_ticket_is_recorded_as_one_and_still_waits_for_a_reviewer()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var agent = new FakeNOpenCode()
            .RoutingAs(WorkItemKind.Build, "the issue names one endpoint and the test that proves it")
            .Producing("src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = await Intaken(host);

        await host.Settle();

        var routed = host.Store.Get(workItem.Id)!;
        Assert.Equal(WorkItemKind.Build, routed.Kind);
        Assert.Equal("the issue names one endpoint and the test that proves it", routed.RouteReason);

        // Still in Backlog, because a routed ticket is still waiting for the click a
        // Backlog work item has always waited for. Routing is not the gate and does not
        // become one.
        Assert.Equal(Swimlane.Backlog, routed.Swimlane);

        // No round against the ceiling: a round that read an issue is not an attempt to
        // build anything, and the board's "round 1 of 3" has to mean that.
        Assert.Equal(0, routed.RoundCount);
        Assert.Empty(host.Store.Rounds(workItem.Id));

        // And a build ticket asks for nothing to be filed, so nothing was.
        Assert.Empty(host.GitHub.IssuesCreated);
        Assert.Null(routed.RoutingIssue);
    }

    [Fact]
    public async Task An_issue_routed_as_needing_grilling_gets_a_question_issue_before_it_is_recorded_as_waiting()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var agent = new FakeNOpenCode().RoutingAs(
            WorkItemKind.Grill,
            "the shape is clear and two decisions about it are not",
            [new ProposedQuestion("does a draft count as published?", "it changes what a click means")]);
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = await Intaken(host);

        await host.Settle();

        var routed = host.Store.Get(workItem.Id)!;
        Assert.Equal(WorkItemKind.Grill, routed.Kind);
        Assert.Equal(Swimlane.Waiting, routed.Swimlane);

        // The question set is a real issue, a child of the maintainer's own, carrying the
        // round's words and an instruction that the answer comes back as a comment.
        var asked = Assert.Single(host.GitHub.IssuesCreated);
        Assert.Equal(workItem.IssueNumber, asked.Parent);
        Assert.Contains("Questions:", asked.Title, StringComparison.Ordinal);
        Assert.Equal(asked.Number, routed.RoutingIssue);

        // And the maintainer's issue is still open, because a merge is what closes an
        // issue and a Grill has shipped nothing. For a while this closed it, saying where
        // the work went, and the maintainer read a closed issue over work that had not
        // started — the claim the factory's close-on-merge rule refuses to make anywhere.
        Assert.Empty(host.GitHub.IssuesClosed);
    }

    [Fact]
    public async Task An_answer_on_the_questions_issue_hands_the_work_back_and_the_next_route_sees_it()
    {
        // The whole hand-back, in the order a human lives it: the factory asks, the human
        // answers in the only place a tracker carries an answer — the conversation — and
        // the work comes back with the answers on it. The clock in these tests starts on
        // 2026-09-28, so an answer dated October is newer than any write the Waiting work
        // item has, which is the test the hand-back actually makes.
        var answer = new IssueComment(
            "dpven",
            "drafts count as published, and the schema stays as it is.",
            new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));

        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var agent = new FakeNOpenCode()
            .RoutingAs(
                WorkItemKind.Grill,
                "the shape is clear and two decisions about it are not",
                [new ProposedQuestion("does a draft count as published?", "it changes what a click means")])
            .RoutingAs(WorkItemKind.Build, "the answers in the conversation settle both decisions");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = await Intaken(host);

        await host.Settle();

        var grilled = host.Store.Get(workItem.Id)!;
        Assert.Equal(Swimlane.Waiting, grilled.Swimlane);
        var question = grilled.RoutingIssue!.Value;

        // No answer yet: a pass reads the conversation and leaves the work item waiting.
        // This is the check that keeps the hand-back from firing on the question itself.
        await host.Step();
        Assert.Equal(Swimlane.Waiting, host.Store.Get(workItem.Id)!.Swimlane);

        // The human answers. The very next step hands the work back: Backlog, unrouted,
        // and a reason a reader can check. The questions issue is kept rather than
        // cleared, because it is where the answers live — the next routing round reads
        // that conversation whole, and it can only do that if the record still points
        // at it.
        host.GitHub.WithComments(RepoUrl, question, answer);

        await host.Step();

        var handed = host.Store.Get(workItem.Id)!;
        Assert.Equal(Swimlane.Backlog, handed.Swimlane);
        Assert.Equal(WorkItemKind.Unrouted, handed.Kind);
        Assert.Equal(question, handed.RoutingIssue);
        Assert.Contains("answers arrived", handed.RouteReason, StringComparison.Ordinal);

        // And the routing round that follows reads the whole conversation — the issue, the
        // question, and the answer — so its second verdict is a decision about the same
        // conversation the human had, not about the body alone.
        await host.Settle();

        var rerouted = host.Store.Get(workItem.Id)!;
        Assert.Equal(WorkItemKind.Build, rerouted.Kind);
        Assert.Equal("the answers in the conversation settle both decisions", rerouted.RouteReason);

        var second = Assert.Single(agent.AskedFor.Skip(1));
        var said = Assert.Single(second.Conversation);
        Assert.Equal("dpven", said.Author);
        Assert.Contains("drafts count as published", said.Body, StringComparison.Ordinal);

        // A hand-back is a Grill's exit, not a general rewind: a work item that is not
        // waiting on a questions issue has nothing that answering would unblock.
        Assert.Throws<InvalidOperationException>(() => host.Store.RecordHandBack(workItem.Id));
    }

    [Fact]
    public async Task A_wayfinder_verdict_becomes_a_map_with_child_tickets_and_wired_blocking_edges()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var agent = new FakeNOpenCode().RoutingAs(
            WorkItemKind.Wayfinder,
            "the destination is clear and none of the way is",
            map: new ProposedMap(
                "a schema where one write is one fact",
                "read the ADR on irreversibility first",
                ["what to do about the audit table"],
                ["backfilling history"],
                [
                    new ProposedTicket("Decide the unit of write", "one row or one document?", []),
                    new ProposedTicket("Decide the read shape", "what does a reader see mid-write?", ["Decide the unit of write"]),
                ]));
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = await Intaken(host);

        await host.Settle();

        var routed = host.Store.Get(workItem.Id)!;
        Assert.Equal(WorkItemKind.Wayfinder, routed.Kind);
        Assert.Equal(Swimlane.Waiting, routed.Swimlane);

        // Map, then its two tickets as children of the map — not of the maintainer's issue,
        // which is the whole difference between a map and two loose tickets.
        Assert.Equal(3, host.GitHub.IssuesCreated.Count);
        var map = host.GitHub.IssuesCreated[0];
        Assert.Equal(workItem.IssueNumber, map.Parent);
        Assert.Contains("Map:", map.Title, StringComparison.Ordinal);

        foreach (var ticket in host.GitHub.IssuesCreated.Skip(1))
        {
            Assert.Equal(map.Number, ticket.Parent);
        }

        // And the edge, wired in the second pass once both tickets have numbers: the read
        // shape cannot be decided until the unit of write is, so it is the blocked one.
        var edge = Assert.Single(host.GitHub.BlockingEdges);
        Assert.Equal(host.GitHub.IssuesCreated[2].Number, edge.Blocked);
        Assert.Equal(host.GitHub.IssuesCreated[1].Number, edge.Blocker);
    }

    [Fact]
    public async Task A_map_whose_blocker_names_no_ticket_is_still_filed_and_says_which_edge_is_missing()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var log = new RecordedLog();
        var agent = new FakeNOpenCode().RoutingAs(
            WorkItemKind.Wayfinder,
            "the way is not visible",
            map: new ProposedMap(
                "somewhere",
                string.Empty,
                [],
                [],
                [new ProposedTicket("Decide the thing", "the real one", ["a ticket nobody wrote"])]));
        await using var host = await FactoryHost.RecordingAsync(root, log, new FactoryMetrics(), agent: agent);

        await Intaken(host);
        await host.Settle();

        // The map is filed. One bad edge costs the edge, not the map — a map that was not
        // filed at all would lose the work item's only account of what the factory did.
        Assert.Equal(2, host.GitHub.IssuesCreated.Count);
        Assert.Empty(host.GitHub.BlockingEdges);
        Assert.Equal(Swimlane.Waiting, host.Store.Get(host.Store.List()[0].Id)!.Swimlane);

        Assert.Contains(
            log.Records,
            record => record.Message.Contains("a ticket nobody wrote", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_verdict_the_factory_cannot_read_parks_the_work_item_with_the_reason_on_it()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var agent = new FakeNOpenCode()
            .RoutingThatCannotAnswer("the round wrote a verdict in a shape this build does not read");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = await Intaken(host);

        await host.Settle();

        var parked = host.Store.Get(workItem.Id)!;
        Assert.Equal(WorkItemKind.Unrouted, parked.Kind);
        Assert.Equal(Swimlane.Escalated, parked.Swimlane);
        Assert.Equal("the round wrote a verdict in a shape this build does not read", parked.RouteReason);

        // And it is not asked again. A parked work item is finished by a human, and a
        // routing round that has already failed to answer is not going to answer when asked
        // a second time.
        await host.Settle();
        Assert.Single(agent.AskedFor);
    }

    [Fact]
    public async Task Questions_that_could_not_be_filed_leave_a_park_rather_than_a_work_item_waiting_for_nothing()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var agent = new FakeNOpenCode().RoutingAs(
            WorkItemKind.Grill,
            "the shape is clear and two decisions about it are not",
            [new ProposedQuestion("does a draft count as published?", "it changes what a click means")]);
        var github = new FakeGitHub().RefusingToWriteIssues(
            FailureClass.Permanent,
            "the credential can read issues but not create them");

        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github);
        var workItem = await Intaken(host);

        await host.Settle();

        // The order is the whole of this test. The verdict exists, the questions do not, and
        // a work item in Waiting with no issue to answer on is a card that says the factory
        // needs something and gives nobody anywhere to give it — so it parks, and says so.
        var parked = host.Store.Get(workItem.Id)!;
        Assert.Equal(Swimlane.Escalated, parked.Swimlane);
        Assert.Equal(WorkItemKind.Unrouted, parked.Kind);
        Assert.Contains("could not file", parked.RouteReason!, StringComparison.Ordinal);
        Assert.Contains("can read issues but not create them", parked.RouteReason!, StringComparison.Ordinal);
        Assert.Empty(host.GitHub.IssuesCreated);
    }

    [Fact]
    public async Task A_routing_round_that_never_finished_parks_the_work_item_rather_than_being_retried()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var agent = new FakeNOpenCode().FailingTransiently().FailingTransiently().FailingTransiently();
await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = await Intaken(host);

        // The backoff is a comparison against the clock rather than a wait, so a retry
        // needs the clock moved rather than the test slept — and the round holds its slot
        // across it, which is why three attempts do not need six slots.
        for (var attempt = 0; attempt < FactoryConstants.TransientRetryAttempts; attempt++)
        {
            await host.Settle();
            host.Clock.Advance(FactoryConstants.RetryBackoff(attempt + 1));
        }

        await host.Settle();

        var parked = host.Store.Get(workItem.Id)!;
        Assert.Equal(Swimlane.Escalated, parked.Swimlane);
        Assert.Contains("transiently", parked.RouteReason!, StringComparison.Ordinal);

        // Three attempts, holding one slot, and then a human — the same shape as a build
        // round that would not start, and the same reasoning: nothing about a second set of
        // three would differ.
        Assert.Equal(3, agent.AskedFor.Count);
        Assert.All(agent.AskedFor, round => Assert.Equal(RoundPurpose.Route, round.Purpose));

        // And not one of them is a round against the ceiling.
        Assert.Empty(host.Store.Rounds(workItem.Id));
    }

    /// <summary>
    /// A work item taken in by the real poller, so the ids and the base branch are the
    /// store's own rather than a fixture's.
    /// </summary>
    private static async Task<WorkItem> Intaken(FactoryHost host)
    {
        host.GitHub.WithRepository(RepoUrl, "main", OpenIssue.Plain(42, "the issue being routed", Body));

        await host.PollAsync();

        return Assert.Single(host.Store.List());
    }
}
