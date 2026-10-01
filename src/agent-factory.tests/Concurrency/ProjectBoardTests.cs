namespace AgentFactory.Tests.Concurrency;

using AgentFactory.Rounds;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;

/// <summary>
/// The board's visible half of the container budget: grouped and filtered by project.
///
/// <para>
/// DESIGN.md says "the board can group or filter work items by project", and it is the
/// only way a reviewer can see what the budget is doing to a factory serving several
/// repositories. A work item sitting in Frontier is queued rather than refused, and a
/// reviewer who cannot tell whose work item that is cannot tell whether the factory is
/// working on their project or on somebody else's.
/// </para>
/// <para>
/// Grouping is rendered, not a client-side toggle over markup the board did not have: a
/// filter that only existed in JavaScript would leave the page a reader sees with one
/// project's work items in it and a control claiming otherwise. Both are read back over
/// the board's own HTTP surface here, which is the only way to be sure a reviewer would
/// see what the test saw.
/// </para>
/// </summary>
public class ProjectBoardTests
{
    private const string Nexus = "https://github.com/NaniSoft/nexus";
    private const string Alpha = "https://github.com/NaniSoft/alpha";
    private const string IssueBody = "What the issue says, in the maintainer's words.";

    [Fact]
    public async Task The_board_groups_work_items_by_project_in_every_lane()
    {
        using var root = TwoProjects();
        var agent = new FakeNOpenCode()
            .Producing("src/Index.cs +12 -3", "Nexus change.")
            .Producing("src/Other.cs +4 -0", "Alpha change.")
            .Stuck()
            .Stuck();
        await using var host = await FactoryHost.StartAsync(root, agent: agent);

        var nexus = await Take(host, Nexus, 42, "nexus", "A work item on nexus");
        var alpha = await Take(host, Alpha, 7, "alpha", "A work item on alpha");
        await host.Settle();

        // Two more work items, one per project, and both rounds are ones that will not come
        // back — so they take both containers and the two after them genuinely wait. Taken
        // after the first pair have been built so the budget is empty when they arrive.
        var building = await Take(host, Alpha, 8, "alpha", "A long build on alpha");
        var alsoBuilding = await Take(host, Nexus, 43, "nexus", "Another long build on nexus");
        await host.Settle();

        var queued = await Take(host, Nexus, 44, "nexus", "Waiting for a container on nexus");
        var alsoQueued = await Take(host, Alpha, 9, "alpha", "Also waiting for a container on alpha");
        await host.Settle();

        // One of each project is in Review, one of each is building and one of each is
        // queued. Two projects and three states each, which is the board this ticket is for:
        // the lanes are the spine and grouping is inside them, so a reviewer's reading of
        // "which state is this in" is unchanged by there being several projects on the board.
        var board = await Board.ReadAsync(host.Board);
        Assert.Equal(Swimlane.Review, SwimlaneOf(host, nexus.Id));
        Assert.Equal(Swimlane.Review, SwimlaneOf(host, alpha.Id));
        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, building.Id));
        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, alsoBuilding.Id));
        Assert.Equal(Swimlane.Frontier, SwimlaneOf(host, queued.Id));
        Assert.Equal(Swimlane.Frontier, SwimlaneOf(host, alsoQueued.Id));

        // Two projects in Review, each in its own group, and no group holding the other's
        // cards. The count is on the group because "how much of this lane is mine" is the
        // question a reviewer is asking when they group by project.
        Assert.Equal(["alpha", "nexus"], Board.ValuesOf(board.Swimlane("Review"), "data-project-group"));
        Assert.Equal(["1", "1"], Board.ValuesOf(board.Swimlane("Review"), "data-project-count"));
        // Each project's cards are in its own group in this lane, and no group is holding
        // the other's. Read inside the lane, because the same project has a group in every
        // lane and a reviewer asking "which of these Review cards are mine" is asking
        // about this lane.
        Assert.Contains(nexus.Id.ToString(), board.ProjectGroup("nexus", "Review"), StringComparison.Ordinal);
        Assert.Contains(alpha.Id.ToString(), board.ProjectGroup("alpha", "Review"), StringComparison.Ordinal);
        Assert.DoesNotContain(alpha.Id.ToString(), board.ProjectGroup("nexus", "Review"), StringComparison.Ordinal);
        Assert.DoesNotContain(nexus.Id.ToString(), board.ProjectGroup("alpha", "Review"), StringComparison.Ordinal);

        // And the same in every other lane, including the two the budget is really about:
        // the one where a container is busy, and the one where a work item is waiting for
        // one. Both projects appear in both, which is the fairness rule made visible — a
        // project is not starved by another's build, and the board shows that rather than
        // asserting it.
        Assert.Equal(["alpha", "nexus"], Board.ValuesOf(board.Swimlane("InProgress"), "data-project-group"));
        Assert.Equal(["alpha", "nexus"], Board.ValuesOf(board.Swimlane("Frontier"), "data-project-group"));
        Assert.Equal(["1", "1"], Board.ValuesOf(board.Swimlane("Frontier"), "data-project-count"));
    }

    [Fact]
    public async Task The_board_can_be_narrowed_to_one_project()
    {
        using var root = TwoProjects();
        var agent = new FakeNOpenCode()
            .Producing("src/Index.cs +12 -3", "Nexus change.")
            .Producing("src/Other.cs +4 -0", "Alpha change.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);

        var nexus = await Take(host, Nexus, 42, "nexus", "A work item on nexus");
        var alpha = await Take(host, Alpha, 7, "alpha", "A work item on alpha");
        await host.Settle();

        // Unfiltered, both are there and neither project is in force.
        var all = await Board.ReadAsync(host.Board);
        Assert.Equal(2, Board.ValuesOf(all.Swimlane("Review"), "data-work-item").Count);
        Assert.Equal(string.Empty, all.ProjectInForce());

        // Narrowed to nexus: nexus's card and nobody else's. Read over HTTP with the query
        // string a reviewer would use, so the filter has to be the board's own.
        var narrowed = await Board.ReadForAsync(host.Board, "nexus");
        Assert.Equal("nexus", narrowed.ProjectInForce());
        Assert.Equal([nexus.Id.ToString()], Board.ValuesOf(narrowed.Swimlane("Review"), "data-work-item"));
        Assert.Equal(["nexus"], Board.ValuesOf(narrowed.Swimlane("Review"), "data-project-group"));
        Assert.DoesNotContain(alpha.Id.ToString(), narrowed.Swimlane("Review"), StringComparison.Ordinal);

        // The lanes themselves are still rendered: a filtered board is a board, not a
        // shorter one, and a lane that has gone missing reads as "nothing is in it" rather
        // than "this is not your project".
        Assert.NotEqual(string.Empty, narrowed.Swimlane("Done"));
        Assert.NotEqual(string.Empty, narrowed.Swimlane("Escalated"));

        // And every project is always on offer, including the one in force, so a reviewer
        // can get back to the whole board without a browser's back button.
        Assert.Equal(["", "alpha", "nexus"], narrowed.ProjectFilters());
    }

    [Fact]
    public async Task A_filtered_board_still_offers_the_three_decisions_and_still_refuses_the_fourth()
    {
        // The filter is a way of looking, never a way of deciding. A project filter that
        // narrowed the decision set would be policy on the page, and the loop is what owns
        // policy (ADR-0005): what a reviewer can press has to be the same three on a
        // filtered board as on the whole one, and a post made by hand has to be refused
        // the same way.
        using var root = TwoProjects();
        var github = new FakeGitHub().Merging();
        var agent = new FakeNOpenCode()
            .Producing("src/Index.cs +12 -3", "Nexus change.")
            .Producing("src/Other.cs +4 -0", "A second nexus change.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github);

        var nexus = await Take(host, Nexus, 42, "nexus", "A work item on nexus");
        var other = await Take(host, Nexus, 43, "nexus", "Another work item on nexus");
        await host.Settle();
        Assert.Equal(Swimlane.Review, SwimlaneOf(host, nexus.Id));
        Assert.Equal(Swimlane.Review, SwimlaneOf(host, other.Id));

        var narrowed = await Board.ReadForAsync(host.Board, "nexus");
        Assert.Equal(
            ["approve", "request-changes", "reject"],
            Board.ValuesOf(narrowed.DecisionFormFor(nexus.Id), "data-decision"));

        // A post made by hand is refused for what it is asking, and the filter changes
        // nothing about that. It goes first, while the work item is still in Review and the
        // board still has a form to post from — otherwise the post would be refused for
        // having no token and the test would be asserting the wrong refusal.
        using (var response = await Board.PostByHandForProjectAsync(
                   host.Board, "nexus", nexus.Id, "shelve", "Some words."))
        {
            var rendered = await Board.ReadAsync(response);

            Assert.Contains(
                "not one of the three decisions",
                rendered.Refusal()!,
                StringComparison.Ordinal);

            // The refusal is rendered on the page the reviewer was looking at, and that
            // page is still the filtered one rather than the whole board.
            Assert.Equal("nexus", rendered.ProjectInForce());
        }

        Assert.Empty(host.Store.Decisions(nexus.Id));
        Assert.Equal(Swimlane.Review, SwimlaneOf(host, nexus.Id));

        // And a real decision made through the same filtered board is a decision: the work
        // item moves, and it moves where the loop said rather than where the page said. The
        // redirect keeps the reviewer on the project they were looking at, because being
        // dropped onto the whole board by their own click would be the filter's one real
        // cost and it is cheap not to pay it.
        using (var response = await Board.DecideForProjectAsync(
                   host.Board, "nexus", nexus.Id, "request-changes", "Move the check inside."))
        {
            var rendered = await Board.ReadAsync(response);

            Assert.Null(rendered.Refusal());
            Assert.Equal("nexus", rendered.ProjectInForce());
        }

        Assert.Equal(Swimlane.Frontier, SwimlaneOf(host, nexus.Id));
        Assert.Equal(
            [Decision.RequestChanges],
            host.Store.Decisions(nexus.Id).Select(decision => decision.Decision));

        // The second work item on the same project is still waiting on a reviewer, and the
        // filter still offers the same three decisions on it. The filter narrows which work
        // items are shown; it does not narrow what a reviewer may do about one.
        var after = await Board.ReadForAsync(host.Board, "nexus");
        Assert.Equal(
            ["approve", "request-changes", "reject"],
            Board.ValuesOf(after.DecisionFormFor(other.Id), "data-decision"));

        // And the work item that was sent back is not offered anything, because it is in
        // Frontier rather than in a lane a reviewer can decide about — the filter is not
        // what took its buttons away.
        Assert.Equal(string.Empty, after.DecisionFormFor(nexus.Id));
        Assert.Equal(Swimlane.Frontier, SwimlaneOf(host, nexus.Id));
    }

    [Fact]
    public async Task A_filtered_board_says_how_much_of_the_container_budget_is_in_use()
    {
        // A reviewer watching a work item sit in Frontier is asking whether the factory is
        // working or wedged, and the budget is the answer. Without it a bounded machine and
        // a stuck one look exactly the same, and the fact that the bound exists at all is
        // only in a source file.
        using var root = TwoProjects();
        var agent = new FakeNOpenCode().Stuck().Stuck();
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        await Take(host, Nexus, 42, "nexus", "One long build on nexus");
        await Take(host, Alpha, 7, "alpha", "One long build on alpha");

        // Nothing started yet, and the board says zero rather than leaving it to be guessed.
        var before = await Board.ReadAsync(host.Board);
        Assert.Equal("0", before.Rendered("data-container-budget", "budget", "data-in-flight"));
        Assert.Equal("2", before.Rendered("data-container-budget", "budget", "data-budget"));

        await host.Settle();

        var during = await Board.ReadAsync(host.Board);
        Assert.Equal("2", during.Rendered("data-container-budget", "budget", "data-in-flight"));
        Assert.Contains(
            "2 of 2 worker containers in use",
            during.Read("Frontier") + during.Html,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_project_the_factory_is_not_serving_is_still_filterable()
    {
        // A project file that has been removed leaves work items behind — rounds run,
        // decisions made, a card a reviewer is judging. Hiding those because the project is
        // no longer configured would lose the record, and a filter that could not select
        // them would be a filter that quietly lost them.
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var agent = new FakeNOpenCode().Producing("src/Index.cs +12 -3", "A change.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);

        var gone = await Take(host, "https://github.com/NaniSoft/retired", 5, "retired", "Built by a project that has since gone");
        await host.Settle();
        Assert.Equal(Swimlane.Review, SwimlaneOf(host, gone.Id));

        var board = await Board.ReadForAsync(host.Board, "retired");
        Assert.Equal("retired", board.ProjectInForce());
        Assert.Equal([gone.Id.ToString()], Board.ValuesOf(board.Swimlane("Review"), "data-work-item"));
    }

    [Fact]
    public async Task A_filter_naming_a_project_with_nothing_on_it_shows_nothing_and_says_so_by_omission()
    {
        // A name the factory has never heard of narrows the board to nothing rather than
        // being refused or quietly ignored into showing everything. Showing everything
        // would be the worse of the two: a reviewer who asked to see one project and got
        // all of them would conclude the filter had worked.
        using var root = TwoProjects();
        var agent = new FakeNOpenCode().Producing("src/Index.cs +12 -3", "Nexus change.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        await Take(host, Nexus, 42, "nexus", "A work item on nexus");
        await host.Settle();

        var board = await Board.ReadForAsync(host.Board, "a project nobody serves");
        Assert.Empty(Board.ValuesOf(board.Swimlane("Review"), "data-work-item"));
        Assert.Equal("a project nobody serves", board.ProjectInForce());

        // And it says so, because an empty board is two different facts and a reviewer
        // cannot tell them apart: this project has nothing waiting, or the factory has
        // never heard of it. The second would look like the first working perfectly.
        Assert.False(board.ProjectIsKnown());
        Assert.Contains("never heard of it", board.Html, StringComparison.Ordinal);

        // The lanes are still drawn, so the emptiness reads as an answer rather than as a
        // broken page.
        Assert.NotEqual(string.Empty, board.Swimlane("Review"));
    }

    private static FactoryRoot TwoProjects() => FactoryRoot.Create()
        .WithProjectFile("alpha.yaml", ProjectFile.For("alpha", Alpha))
        .WithProjectFile("nexus.yaml", ProjectFile.For("nexus", Nexus));

    private static async Task<WorkItem> Take(FactoryHost host, string repoUrl, int issueNumber, string project, string title)
    {
        var workItem = host.Store
            .Intake(project, repoUrl, issueNumber, title, IssueBody, "main")
            .WorkItem;
        await host.PromoteAsync(workItem.Id);
        return workItem;
    }

    private static Swimlane SwimlaneOf(FactoryHost host, Guid id) => host.Store.Get(id)!.Swimlane;
}
