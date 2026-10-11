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
///
/// <para>
/// <b>This file changed when the routing layer landed (ADR-0013), and the gate did not
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
        // The gate, end to end at the surface: a routed build ticket's Backlog card carries
        // the build button, and the click goes through the page to the orchestrator — the
        // same component that applies every other transition — and the item moves.
        //
        // **Which now has a condition in front of it, and the condition is the point of
        // this test as much as the button is (ADR-0013).** An issue nobody has looked at is
        // not a build ticket, so its card carries no button: rendering one would be offering
        // a reviewer a click that the loop is about to refuse.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main")
            .WorkItem;

        // Before: in Backlog, nobody has looked at it, and the board says so on the card
        // rather than leaving the absence to be inferred from a missing button.
        var before = await (await host.Board.GetAsync("/?project=nexus")).Content.ReadAsStringAsync();
        Assert.DoesNotContain($"data-build=\"{workItem.Id}\"", before, StringComparison.Ordinal);
        Assert.Contains("Nobody has looked at this issue yet", before, StringComparison.Ordinal);

        host.Route(workItem.Id, WorkItemKind.Build, "the issue names one endpoint and one test");

        var html = await (await host.Board.GetAsync("/?project=nexus")).Content.ReadAsStringAsync();
        Assert.Contains($"data-build=\"{workItem.Id}\"", html, StringComparison.Ordinal);

        const string marker = "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "the board rendered no antiforgery token");
        var token = html[(start + marker.Length)..];
        token = token[..token.IndexOf('"', StringComparison.Ordinal)];

        using var form = new System.Net.Http.FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
            new KeyValuePair<string, string>("workItemId", workItem.Id.ToString()),
        ]);
        using var response = await host.Board.PostAsync("/?handler=Build&project=nexus", form);
        Assert.True(response.IsSuccessStatusCode, $"the build POST was refused: {response.StatusCode}");

        Assert.Equal(Swimlane.Frontier, host.Store.Get(workItem.Id)!.Swimlane);
    }

    /// <summary>
    /// The other half of the same thing, and it is about what the board *cannot* be made to
    /// do rather than what the loop refuses.
    ///
    /// <para>
    /// An issue nobody has looked at has no build button on its card, so there is no form
    /// to copy a token from and nothing on the page that names the work item as buildable.
    /// That is the surface half. The loop refuses the same work item if a caller asks anyway,
    /// which `An_issue_nobody_has_looked_at_is_not_built_and_cannot_be_accepted` asserts —
    /// and it is asserted separately on purpose: a test that posted the form by hand would be
    /// testing the antiforgery token as much as the gate, and would pass for the wrong reason
    /// if the two ever disagreed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task There_is_no_way_to_accept_an_issue_from_the_board_before_it_has_been_routed()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main")
            .WorkItem;

        var html = await (await host.Board.GetAsync("/?project=nexus")).Content.ReadAsStringAsync();

        // No form for it, and nothing anywhere on the page that could be turned into one:
        // the work item's id appears in its own card and nowhere as a build target.
        Assert.DoesNotContain("data-build=", html, StringComparison.Ordinal);
        Assert.DoesNotContain($"value=\"{workItem.Id}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("handler=Build", html, StringComparison.Ordinal);

        // And the card says why there is no button, in words rather than by absence. Quoted from
        // `HowToReadTheRoute`, which is where that judgement lives — the view renders what
        // that class says and decides nothing here.
        Assert.Contains("Nobody has looked at this issue yet", html, StringComparison.Ordinal);
        Assert.Contains("will decide whether it is a build ticket", html, StringComparison.Ordinal);
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
