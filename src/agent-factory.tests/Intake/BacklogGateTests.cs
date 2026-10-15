namespace AgentFactory.Tests.Intake;

using AgentFactory.Domain.Rounds;
using AgentFactory.Domain.WorkItems;
using AgentFactory.Loop;
using AgentFactory.Rounds;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http;
using System.Net;

/// <summary>
/// The gate between Backlog and Frontier (#40): intake is indiscriminate, and the board
/// is where a human decides what is worth building — which is only true if nothing is
/// built until a reviewer says so.
///
/// <para>
/// <b>This file changed when the routing layer landed (ADR-0015), and the gate did not
/// move.</b> What moved is that "built" now has a step in front of it: an issue nobody has
/// looked at is looked at, and only then can it be accepted. Both halves are asserted here
/// separately, because they are two different refusals and conflating them is how the gate
/// would quietly become routing's job.
/// </para>
/// </summary>
public class BacklogGateTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";

    private const string IssueBody = "What the issue says, in the maintainer's words.";

    [Fact]
    public async Task A_work_item_in_backlog_is_not_built_until_a_reviewer_accepts_it()
    {
        using var root = FactoryRoot.Create();
        // The routing round says this issue is a build ticket, so the only thing standing
        // between it and the build is the reviewer's click. Nothing else is scripted: a
        // build round asked for here would find no script and fail the test.
        var agent = new FakeNOpenCode().RoutingAs(WorkItemKind.Build, "the issue is a build ticket");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main")
            .WorkItem;

        // Whatever the machine does on its own — the poller's turn, the loop's step, the
        // heartbeat's tick — a work item nobody has accepted stays where intake put it.
        // The one container it may cost is the routing round that read the issue, which
        // builds nothing and is not charged to the round ceiling (ADR-0015).
        await host.Settle();
        await host.Tick();

        Assert.Equal(Swimlane.Backlog, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Equal(0, host.Store.Get(workItem.Id)!.RoundCount);
        Assert.All(agent.AskedFor, round => Assert.Equal(RoundPurpose.Route, round.Purpose));
    }

    [Fact]
    public async Task An_issue_nobody_has_looked_at_is_not_built_and_cannot_be_accepted()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode()
            .RoutingAs(WorkItemKind.Build, "the issue names one endpoint and one test")
            .Producing("src/Index.cs +12 -3", "First attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main")
            .WorkItem;

        // The reviewer's click is refused, and it is refused because nobody has looked at
        // this issue yet — not because it is in the wrong lane, and not because the work
        // item is in the build already.
        Assert.False(await host.PromoteUnroutedAsync(workItem.Id));
        Assert.Equal(Swimlane.Backlog, host.Store.Get(workItem.Id)!.Swimlane);

        // And the one container an unrouted issue is allowed to cost is the routing round
        // that looks at it — which builds nothing. The work item is still in Backlog, still
        // with no rounds against it, and the only round the factory asked for was the one
        // that read the issue.
        await host.Settle();
        await host.Tick();

        Assert.Equal(Swimlane.Backlog, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Equal(0, host.Store.Get(workItem.Id)!.RoundCount);
        Assert.Equal(WorkItemKind.Build, host.Store.Get(workItem.Id)!.Kind);
        Assert.Equal(RoundPurpose.Route, Assert.Single(agent.AskedFor).Purpose);
    }

    [Fact]
    public async Task A_reviewers_acceptance_moves_a_routed_backlog_item_into_the_build()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main")
            .WorkItem;

        host.Route(workItem.Id, WorkItemKind.Build, "the issue names what to change");

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
    public async Task Something_that_is_not_a_build_ticket_cannot_be_accepted_however_much_a_reviewer_asks()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main")
            .WorkItem;

        // Routed as needing a map, and put back in Backlog so the only thing standing
        // between it and the build is the reviewer's click.
        host.Route(workItem.Id, WorkItemKind.Wayfinder, "the way to the destination is not visible", routingIssue: 900);
        Assert.Equal(Swimlane.Waiting, host.Store.Get(workItem.Id)!.Swimlane);

        Assert.False(await host.PromoteUnroutedAsync(workItem.Id));
        Assert.NotEqual(Swimlane.Frontier, host.Store.Get(workItem.Id)!.Swimlane);
    }

    [Fact]
    public async Task The_board_offers_the_acceptance_only_for_something_it_is_willing_to_build()
    {
        // The gate, end to end at the surface: a routed build ticket's Backlog card offers
        // the acceptance control, and the click goes through the endpoint to the orchestrator
        // — the same component that applies every other transition — and the item moves.
        //
        // **Which now has a condition in front of it, and the condition is the point of
        // this test as much as the control is (ADR-0015).** An issue nobody has looked at is
        // not a build ticket, so its card offers no acceptance: rendering one would be
        // offering a reviewer a click that the loop is about to refuse.
        //
        // The board here is the factory's own judgement read back over `/api/board` — the
        // same read the admin app makes — so `data-build` is the factory's `offersAcceptance`
        // and not a renderer's guess about a lane.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main")
            .WorkItem;

        // Before: in Backlog, nobody has looked at it, and the board says so on the card
        // rather than leaving the absence to be inferred from a missing control.
        var before = await Board.ReadAsync(host.Board);
        Assert.DoesNotContain($"data-build=\"{workItem.Id}\"", before.Html, StringComparison.Ordinal);
        Assert.Contains($"data-offers-acceptance=\"false\"", before.Html, StringComparison.Ordinal);
        Assert.Contains("Nobody has looked at this issue yet", before.Html, StringComparison.Ordinal);

        host.Route(workItem.Id, WorkItemKind.Build, "the issue names one endpoint and one test");

        var after = await Board.ReadAsync(host.Board);
        Assert.Contains($"data-build=\"{workItem.Id}\"", after.Html, StringComparison.Ordinal);
        Assert.Contains($"data-offers-acceptance=\"true\"", after.Html, StringComparison.Ordinal);

        using var response = await Board.AcceptAsync(host.Board, workItem.Id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var answer = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"applied\":true", answer, StringComparison.Ordinal);
        Assert.Equal(Swimlane.Frontier, host.Store.Get(workItem.Id)!.Swimlane);
    }

    [Fact]
    public async Task There_is_no_way_to_accept_an_issue_from_the_board_before_it_has_been_routed()
    {
        // The other half of the same thing, and it is about what the board *cannot* be made
        // to do rather than what the loop refuses. An issue nobody has looked at has no
        // acceptance control on its card, and the card says why in words rather than by
        // absence. The loop refuses the same work item if a caller asks anyway, which
        // `An_issue_nobody_has_looked_at_is_not_built_and_cannot_be_accepted` asserts — and
        // it is asserted separately on purpose: this is the surface half.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        host.Store
            .Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main");

        var board = await Board.ReadAsync(host.Board);

        // No control anywhere on the board that could be turned into one, and the card
        // carries the route reason that says why. Quoted from `HowToReadTheRoute`, which is
        // where that judgement lives — the API serialises what that class says and decides
        // nothing here.
        Assert.DoesNotContain("data-build=", board.Html, StringComparison.Ordinal);
        Assert.Contains("Nobody has looked at this issue yet", board.Html, StringComparison.Ordinal);
        Assert.Contains("will decide whether it is a build ticket", board.Html, StringComparison.Ordinal);
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
