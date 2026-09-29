namespace AgentFactory.Tests.Intake;

using AgentFactory.Failures;
using AgentFactory.GitHub;
using AgentFactory.Polling;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;

/// <summary>
/// The board's account of intake: never polled, polled, and failing — three rendered states
/// rather than one empty Backlog.
/// </summary>
/// <remarks>
/// <para>
/// This is the surface #16 found. The board said "Serving 2 projects" above an empty Backlog
/// and said nothing at all about the fact that not one poll had ever succeeded, because the
/// GitHub client was refused every request it made — and a reviewer looking at that board
/// cannot tell "polled, nothing open" from "has never polled successfully".
/// </para>
/// <para>
/// So this is the same treatment the board already gives a round's diff, where empty,
/// unavailable and absent are three different rendered states with three different tests.
/// Intake now has the same three, one per project, with the state named in the markup
/// (<c>data-intake-state</c>) so an assertion is about what the board claims rather than
/// about a sentence that could be reworded to say the same thing.
/// </para>
/// <para>
/// Every test here reads the board over its real HTTP surface with the real store, the real
/// poller and the real logger. Only the GitHub seam is substituted, and only because intake
/// reads through it.
/// </para>
/// </remarks>
public class IntakeBoardTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";
    private const string Alpha = "https://github.com/NaniSoft/alpha";
    private const string Zeta = "https://github.com/NaniSoft/zeta";

    [Fact]
    public async Task A_project_the_factory_has_never_read_says_so_rather_than_saying_nothing_about_it()
    {
        // The first state. A board with no intake section for a project, or with a row
        // whose state was left blank, is the same board as one that had read it and found
        // nothing open — and that is the confusion this whole file exists to remove.
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        await using var host = await FactoryHost.StartAsync(root);

        // Nothing has been polled: this is a factory that has just started, which is the
        // ordinary first five seconds of every run and the state a reviewer lands on when
        // they open the board.
        Assert.Empty(host.GitHub.Polled);
        Assert.Empty(host.Store.List());

        var board = await Board.ReadAsync(host.Board);

        // The project's own row, and the whole factory's, both saying the same thing.
        Assert.Equal("never-polled", board.Rendered("data-intake-project", "nexus", "data-intake-state"));
        Assert.Equal("never-polled", board.Rendered("data-intake", "intake", "data-intake-state"));
        Assert.Equal("1", board.Rendered("data-intake", "intake", "data-never-polled"));
        Assert.Equal("0", board.Rendered("data-intake", "intake", "data-polled"));

        // In words, because the state is also something a person reads: no count of open
        // issues is claimed, and it says when it will next be read.
        Assert.Contains("not polled yet", board.Html, StringComparison.Ordinal);
        Assert.Equal("next-pass", board.Rendered("data-intake-project", "nexus", "data-again"));
        Assert.Equal(string.Empty, board.Rendered("data-intake-project", "nexus", "data-open-issues"));

        // And the summary does not let an empty Backlog read as an answer while the factory
        // knows nothing about the repository yet.
        Assert.Contains(
            "An empty Backlog does not mean there is nothing to do",
            board.Html,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_project_that_was_polled_and_had_nothing_open_says_that_and_not_that_it_was_never_polled()
    {
        // The second state, and the one a healthy factory with nothing to do is in. It has
        // to be visibly different from the first and from the third, or the distinction is
        // not a distinction.
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var github = new FakeGitHub().WithRepository(RepoUrl, defaultBranch: "main");
        await using var host = await FactoryHost.StartAsync(root, github: github);

        await host.PollAsync();

        Assert.Equal(1, github.TimesPolled(RepoUrl));
        Assert.Empty(host.Store.List());

        var board = await Board.ReadAsync(host.Board);

        Assert.Equal("polled", board.Rendered("data-intake-project", "nexus", "data-intake-state"));
        Assert.Equal("polled", board.Rendered("data-intake", "intake", "data-intake-state"));
        Assert.Equal("1", board.Rendered("data-intake", "intake", "data-polled"));
        Assert.Equal("0", board.Rendered("data-intake", "intake", "data-failing"));

        // The count is on the row, because "polled" alone would leave a reviewer unable to
        // tell a repository with nothing open from one whose issues all arrived and are
        // already on the board.
        Assert.Equal("0", board.Rendered("data-intake-project", "nexus", "data-open-issues"));
        Assert.Contains("nothing is open", board.Html, StringComparison.Ordinal);
        Assert.Equal("next-pass", board.Rendered("data-intake-project", "nexus", "data-again"));

        // And the summary is the healthy one: intake is working, which is now a stated fact
        // rather than the absence of a complaint.
        Assert.Contains("Intake is reading every project", board.Html, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "An empty Backlog does not mean there is nothing to do",
            board.Html,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_project_that_was_polled_with_open_issues_says_how_many()
    {
        // The same state as above with something to report, so the count on a polled row is
        // a number rather than a constant.
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var github = new FakeGitHub().WithRepository(
            RepoUrl,
            defaultBranch: "main",
            OpenIssue.Plain(1, "The first issue", "A body."),
            OpenIssue.Plain(2, "The second issue", "A body."));
        await using var host = await FactoryHost.StartAsync(root, github: github);

        await host.PollAsync();

        var board = await Board.ReadAsync(host.Board);

        Assert.Equal("polled", board.Rendered("data-intake-project", "nexus", "data-intake-state"));
        Assert.Equal("2", board.Rendered("data-intake-project", "nexus", "data-open-issues"));
        Assert.Contains("2 open issue(s)", board.Html, StringComparison.Ordinal);
        Assert.Equal(2, host.Store.List().Count);
    }

    [Fact]
    public async Task A_project_whose_intake_is_failing_is_a_project_fault_and_says_why()
    {
        // The third state, and the one #16's run produced: intake cannot reach the
        // repository at all. It has no work item, so there is no card anywhere on this board
        // to hang it on — which is why it is a *project's* row rather than one of the lanes.
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var github = new FakeGitHub()
            .Failing(RepoUrl, FailureClass.Permanent, "the repository does not exist");
        await using var host = await FactoryHost.StartAsync(root, github: github);

        await host.PollAsync();

        var board = await Board.ReadAsync(host.Board);

        Assert.Equal("failing", board.Rendered("data-intake-project", "nexus", "data-intake-state"));
        Assert.Equal("failing", board.Rendered("data-intake", "intake", "data-intake-state"));
        Assert.Equal("1", board.Rendered("data-intake", "intake", "data-failing"));

        // The whole of it, as fields rather than as a sentence a reader has to parse: which
        // repository, how it failed, how many times, and whether it will be asked again.
        // "never" is the one a reviewer has to be able to see without reading — a project
        // left failing for ever is not a project that will fix itself.
        Assert.Equal(RepoUrl, board.Rendered("data-intake-project", "nexus", "data-repo-url"));
        Assert.Equal("Permanent", board.Rendered("data-intake-project", "nexus", "data-classification"));
        Assert.Equal("1", board.Rendered("data-intake-project", "nexus", "data-failures"));
        Assert.Equal("never", board.Rendered("data-intake-project", "nexus", "data-again"));
        Assert.Equal(string.Empty, board.Rendered("data-intake-project", "nexus", "data-open-issues"));

        // GitHub's own words are on the board, because the operator has to know which fault
        // it is: a repository that is gone and a credential that cannot read it are the same
        // red row and two different jobs.
        Assert.Contains("the repository does not exist", board.Html, StringComparison.Ordinal);
        Assert.Contains("restart the factory", board.Html, StringComparison.Ordinal);

        // And it is nowhere near a lane. There is no work item to put in one, and this is
        // the whole reason a project-level fault is rendered in its own section.
        Assert.Empty(Board.ValuesOf(board.Swimlane("Backlog"), "data-work-item"));
        Assert.Empty(Board.ValuesOf(board.Swimlane("Escalated"), "data-work-item"));
    }

    [Fact]
    public async Task A_transient_intake_failure_says_when_the_project_will_be_read_again()
    {
        // The same rendered state with a different answer inside it, because "failing" and
        // "failing and we are still asking" are different situations and an operator needs
        // to tell them apart: one wants a restart, the other wants patience.
        var clock = new TestClock();
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var github = new FakeGitHub().Failing(RepoUrl, FailureClass.Transient, "the API returned 503");
        await using var host = await FactoryHost.StartAsync(root, clock, github: github);

        await host.PollAsync();

        var board = await Board.ReadAsync(host.Board);

        Assert.Equal("failing", board.Rendered("data-intake-project", "nexus", "data-intake-state"));
        Assert.Equal("Transient", board.Rendered("data-intake-project", "nexus", "data-classification"));

        // A moment, not the word "never" — read as the clock it was measured against rather
        // than as the backoff constant, so the test would fail if the row said something
        // other than when this project is actually next read.
        var again = board.Rendered("data-intake-project", "nexus", "data-again");
        Assert.NotEqual("never", again);
        Assert.Equal(
            (clock.UtcNow + FactoryConstants.PollBackoff(1)).ToString("O"),
            System.Net.WebUtility.HtmlDecode(again));
    }

    [Fact]
    public async Task An_empty_backlog_while_intake_is_broken_cannot_be_read_as_nothing_to_do()
    {
        // **The test that would have caught #16.** Two projects, a healthy one with nothing
        // open and one that cannot be read at all, and so an empty Backlog — exactly the
        // board the first real run produced and could not be trusted: "Serving 2 projects"
        // above an empty lane, with not one poll having ever succeeded.
        //
        // Read against the board as it was before this ticket, every assertion below fails:
        // there is no intake section to read, so `Rendered` cannot find the marker, and the
        // only thing the page said about intake was the count of projects being served.
        using var root = FactoryRoot.Create()
            .WithProjectFile("alpha.yaml", ProjectFile.For("alpha", Alpha))
            .WithProjectFile("nexus.yaml", ProjectFile.For("nexus", RepoUrl));

        var github = new FakeGitHub()
            .WithRepository(Alpha, defaultBranch: "main")
            .Failing(RepoUrl, FailureClass.Permanent, "403: your credential cannot read this repository");
        await using var host = await FactoryHost.StartAsync(root, github: github);

        await host.PollAsync();

        // The precondition, asserted first: the board really is empty. A test that only
        // passed because there was work on it would not be the test this needs.
        Assert.Equal(1, github.TimesPolled(RepoUrl));
        Assert.Equal(1, github.TimesPolled(Alpha));
        Assert.Empty(host.Store.List());

        var board = await Board.ReadAsync(host.Board);
        Assert.Empty(Board.ValuesOf(board.Swimlane("Backlog"), "data-work-item"));

        // **And the fault is on the page, above the lanes, naming the project.** A reviewer
        // cannot see it by reading the lanes, because the lanes are empty and look perfect.
        Assert.Equal("failing", board.Rendered("data-intake", "intake", "data-intake-state"));
        Assert.Equal("failing", board.Rendered("data-intake-project", "nexus", "data-intake-state"));

        // The sentence that ties the two together, which is the claim the board was failing
        // to make: the empty lane and the broken intake are one fact, and the lane alone is
        // a misleading half of it.
        Assert.Contains("cannot be read", board.Html, StringComparison.Ordinal);
        Assert.Contains(
            "An empty Backlog does not mean there is nothing to do while intake is broken",
            board.Html,
            StringComparison.Ordinal);

        // And it is said before the lanes rather than somewhere a reviewer has to look:
        // the fault comes first, the empty Backlog second.
        var fault = board.Html.IndexOf("data-intake-project=\"nexus\"", StringComparison.Ordinal);
        var backlog = board.Html.IndexOf("data-swimlane=\"Backlog\"", StringComparison.Ordinal);

        Assert.True(fault >= 0 && backlog >= 0, "both the fault and the lane have to be on the page to order them");
        Assert.True(fault < backlog, "a fault in intake is above the lanes, not somewhere a reviewer has to go looking");

        // The healthy project is not implicated by it: it is polled, it has nothing open,
        // and it says so. A board that rendered every project as failing because one of them
        // is would be no better than one that rendered none of them as failing.
        Assert.Equal("polled", board.Rendered("data-intake-project", "alpha", "data-intake-state"));
        Assert.Equal("2", board.Rendered("data-intake", "intake", "data-projects"));
        Assert.Equal("1", board.Rendered("data-intake", "intake", "data-polled"));
        Assert.Equal("1", board.Rendered("data-intake", "intake", "data-failing"));
    }

    [Fact]
    public async Task A_board_narrowed_to_one_project_still_reports_a_fault_in_another()
    {
        // The filter is a way of looking at work items, not at the machine. Narrowing to
        // alpha must not hide that nexus cannot be read — a filter that could hide the
        // reason there is nothing on the board is a filter that can make a broken factory
        // look like a working one.
        using var root = FactoryRoot.Create()
            .WithProjectFile("alpha.yaml", ProjectFile.For("alpha", Alpha))
            .WithProjectFile("nexus.yaml", ProjectFile.For("nexus", RepoUrl));

        var github = new FakeGitHub()
            .WithRepository(Alpha, defaultBranch: "main")
            .Failing(RepoUrl, FailureClass.Permanent, "the repository does not exist");
        await using var host = await FactoryHost.StartAsync(root, github: github);

        await host.PollAsync();

        var narrowed = await Board.ReadForAsync(host.Board, "alpha");

        Assert.Equal("alpha", narrowed.ProjectInForce());
        Assert.Equal("failing", narrowed.Rendered("data-intake", "intake", "data-intake-state"));
        Assert.Equal("failing", narrowed.Rendered("data-intake-project", "nexus", "data-intake-state"));
        Assert.Contains("the repository does not exist", narrowed.Html, StringComparison.Ordinal);

        // And what the filter does still narrow is the lanes, so the two halves of the board
        // are not being confused for one another.
        Assert.Equal(string.Empty, narrowed.ProjectGroup("nexus", "Backlog"));
    }

    [Fact]
    public async Task The_board_renders_three_intake_states_and_no_others()
    {
        // The vocabulary, written out rather than read from the enum, and seen on one board
        // rather than across three: a fourth state appearing in the markup would be a state
        // nobody has argued for, and a reader looking at this board should be able to count
        // the ways intake can be without counting enum members.
        //
        // Three projects and two steps of the rotation is how all three land at once. The
        // first project is read and has nothing open; the second fails permanently; and the
        // third has not had its turn yet — which is the state a factory is in between
        // steps, and the one a reviewer lands on when they open a board that has just
        // started.
        using var root = FactoryRoot.Create()
            .WithProjectFile("alpha.yaml", ProjectFile.For("alpha", Alpha))
            .WithProjectFile("nexus.yaml", ProjectFile.For("nexus", RepoUrl))
            .WithProjectFile("zeta.yaml", ProjectFile.For("zeta", Zeta));

        var github = new FakeGitHub()
            .WithRepository(Alpha, defaultBranch: "main")
            .Failing(RepoUrl, FailureClass.Permanent, "the repository does not exist")
            .WithRepository(Zeta, defaultBranch: "main");
        await using var host = await FactoryHost.StartAsync(root, github: github);

        // Two steps, not a pass: alpha's turn and nexus's, and zeta's is still to come.
        Assert.True(await host.IntakeStepAsync());
        Assert.True(await host.IntakeStepAsync());

        var board = await Board.ReadAsync(host.Board);

        Assert.Equal("polled", board.Rendered("data-intake-project", "alpha", "data-intake-state"));
        Assert.Equal("failing", board.Rendered("data-intake-project", "nexus", "data-intake-state"));
        Assert.Equal("never-polled", board.Rendered("data-intake-project", "zeta", "data-intake-state"));

        // The section's own state is the worst of them, so a board says "something here is
        // not working" without a reviewer having to read every row to find out which.
        Assert.Equal("failing", board.Rendered("data-intake", "intake", "data-intake-state"));
        Assert.Equal("1", board.Rendered("data-intake", "intake", "data-polled"));
        Assert.Equal("1", board.Rendered("data-intake", "intake", "data-failing"));
        Assert.Equal("1", board.Rendered("data-intake", "intake", "data-never-polled"));

        // The whole vocabulary, from the markup: the section's state plus one per project,
        // and three distinct values among them. Read rather than asserted from the enum,
        // because the markup is what a reviewer — and the rest of this file's tests — sees.
        Assert.Equal(
            ["failing", "never-polled", "polled"],
            Board.ValuesOf(board.Html, "data-intake-state").Distinct().Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void Intake_knows_which_projects_are_failing_and_the_board_asks_nothing_else()
    {
        // What the board reads is intake's own account of intake, asked for on the render.
        // A page that kept its own copy of that would be a second thing free to disagree
        // with the poller about whether a project is failing, and this board already has
        // one such problem in the filter — so the poller's dependency list is what holds it
        // to the same boundary the loop and the board already have.
        //
        // Nothing new: no clock of its own, no transport, no credential reader, and no way
        // to *change* anything. The board asks it a question and renders the answer, which
        // the whole of its public surface below is: take a turn, take a pass, and say what
        // intake has done (`get_Intake` being the getter behind `Poller.Intake`). A
        // `Move`, a `MergeAsync` or a `RecordDecision` here would be a second way for
        // something to change state outside the loop.
        Assert.Equal(
            ["IWorkItemStore", "IGitHub", "IClock", "ProjectLoadReport", "ILogger`1", "FactoryMetrics"],
            typeof(Poller)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Select(parameter => parameter.ParameterType.Name));

        Assert.Equal(
            ["PassAsync", "StepAsync", "get_Intake"],
            typeof(Poller)
                .GetMethods()
                .Where(method => method.DeclaringType == typeof(Poller))
                .Select(method => method.Name)
                .Order(StringComparer.Ordinal)
                .ToList());
    }
}
