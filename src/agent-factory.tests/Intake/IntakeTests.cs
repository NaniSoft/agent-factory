namespace AgentFactory.Tests.Intake;

using AgentFactory.GitHub;
using AgentFactory.Polling;
using AgentFactory.Rounds;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;

/// <summary>
/// Intake: the factory reads the open issues of every project it serves and turns them
/// into work items in Backlog, one project's turn at a time. The store and the board
/// are the real thing; only GitHub and the clock are substituted, and the loop is not
/// driven, because what is under test here is intake.
/// </summary>
public class IntakeTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";

    private const string Alpha = "https://github.com/NaniSoft/alpha";
    private const string Mullet = "https://github.com/NaniSoft/mullet";
    private const string Zeta = "https://github.com/NaniSoft/zeta";

    [Fact]
    public async Task Every_open_issue_on_a_configured_repository_becomes_a_work_item_in_backlog()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var github = new FakeGitHub().WithRepository(
            RepoUrl,
            defaultBranch: "main",
            OpenIssue.Plain(1, "The first issue", "What the first issue asks for."),
            OpenIssue.Plain(2, "The second issue", "What the second issue asks for."));
        await using var host = await FactoryHost.StartAsync(root, github: github);

        await host.PollAsync();

        var workItems = host.Store.List();
        Assert.Equal(2, workItems.Count);
        Assert.All(workItems, workItem => Assert.Equal(Swimlane.Backlog, workItem.Swimlane));

        var board = await Board.ReadAsync(host.Board);
        Assert.Contains("The first issue", board.Swimlane("Backlog"), StringComparison.Ordinal);
        Assert.Contains("The second issue", board.Swimlane("Backlog"), StringComparison.Ordinal);
        Assert.DoesNotContain("The first issue", board.Swimlane("Frontier"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_work_item_carries_the_issue_it_came_from_and_the_branch_the_repository_is_on()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var github = new FakeGitHub().WithRepository(
            RepoUrl,
            defaultBranch: "release/2.0",
            OpenIssue.Plain(7, "A work item, from an issue", "What the issue says, in the maintainer's words."),
            OpenIssue.Plain(8, "Another issue", "And another body."));
        await using var host = await FactoryHost.StartAsync(root, github: github);

        await host.PollAsync();

        var workItem = host.Store.List().Single(item => item.IssueNumber == 7);
        Assert.Equal("nexus", workItem.Project);
        Assert.Equal(RepoUrl, workItem.RepoUrl);
        Assert.Equal("A work item, from an issue", workItem.IssueTitle);
        Assert.Equal("What the issue says, in the maintainer's words.", workItem.IssueBody);

        // The base is the repository's default branch, resolved through the seam now, and
        // resolved once for the turn rather than once per issue: both issues polled in
        // this pass are built against the same starting point.
        Assert.Equal("release/2.0", workItem.BaseBranch);
        Assert.Equal(1, github.BranchesResolved(RepoUrl));
        Assert.Equal(0, workItem.RoundCount);

        var board = await Board.ReadAsync(host.Board);
        Assert.Equal("7", board.Rendered("data-work-item", workItem.Id.ToString(), "data-issue"));
        Assert.Equal("release/2.0", board.Rendered("data-work-item", workItem.Id.ToString(), "data-base-branch"));
    }

    [Fact]
    public async Task A_project_is_polled_again_only_once_the_poll_interval_has_passed()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var github = new FakeGitHub().WithRepository(
            RepoUrl, defaultBranch: "main", OpenIssue.Plain(1, "One issue", "What it asks for."));
        await using var host = await FactoryHost.StartAsync(root, github: github);

        // The number is written out here rather than read from the constant: a test that
        // advanced by the constant would prove only that the poller compares two values.
        Assert.Equal(TimeSpan.FromSeconds(60), FactoryConstants.PollInterval);

        // The first pass is due the moment the factory starts, so an issue is not left
        // waiting a minute for a factory that has just come up.
        await host.PollAsync();
        Assert.Equal(1, github.TimesPolled(RepoUrl));

        // Short of the interval no pass is due, so nothing is read at all.
        host.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(await host.IntakeStepAsync());
        await host.PollAsync();
        Assert.Equal(1, github.TimesPolled(RepoUrl));

        // At the interval, the next pass runs.
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        await host.PollAsync();
        Assert.Equal(2, github.TimesPolled(RepoUrl));
    }

    [Fact]
    public async Task Projects_take_their_turns_in_file_name_order_derived_from_the_directory()
    {
        using var root = ThreeProjectsWrittenOutOfOrder();
        var github = new FakeGitHub()
            .WithRepository(Alpha, "main", OpenIssue.Plain(1, "An issue on alpha", "A body."))
            .WithRepository(Mullet, "main", OpenIssue.Plain(2, "An issue on mullet", "A body."))
            .WithRepository(Zeta, "main", OpenIssue.Plain(3, "An issue on zeta", "A body."));
        await using var host = await FactoryHost.StartAsync(root, github: github);

        await host.PollAsync();

        // The rotation is the directory sorted by file name, and nothing else. The files
        // were written in the reverse of that order, so filesystem enumeration order
        // cannot be what decided it, and the project *names* are deliberately shifted one
        // place along — a rotation sorted by name instead would come out zeta, alpha,
        // mullet, and the directory's ordering is the one that is claimed.
        Assert.Equal([Alpha, Mullet, Zeta], github.Polled);
        Assert.Equal(
            ["mullet", "zeta", "alpha"],
            host.Store.List().Select(workItem => workItem.Project));
    }

    [Fact]
    public async Task A_step_is_one_projects_turn_and_the_rotation_comes_round_again_from_the_first()
    {
        using var root = ThreeProjectsWrittenOutOfOrder();
        var github = new FakeGitHub()
            .WithRepository(Alpha, "main", OpenIssue.Plain(1, "An issue on alpha", "A body."))
            .WithRepository(Mullet, "main")
            .WithRepository(Zeta, "main");
        await using var host = await FactoryHost.StartAsync(root, github: github);

        // One step, one project: a repository with a thousand open issues cannot take a
        // pass's attention for itself and leave the rest of the rotation waiting.
        Assert.True(await host.IntakeStepAsync());
        Assert.Equal([Alpha], github.Polled);
        Assert.True(await host.IntakeStepAsync());
        Assert.Equal([Alpha, Mullet], github.Polled);

        // The pass is over once the rotation has been round, and the next pass begins at
        // the first project rather than wherever the last one happened to stop.
        Assert.True(await host.IntakeStepAsync());
        Assert.Equal([Alpha, Mullet, Zeta], github.Polled);
        Assert.False(await host.IntakeStepAsync(), "the pass is over and the next one is not due");

        host.Clock.Advance(FactoryConstants.PollInterval);
        Assert.True(await host.IntakeStepAsync());
        Assert.Equal([Alpha, Mullet, Zeta, Alpha], github.Polled);
    }

    [Fact]
    public async Task A_project_that_cannot_be_polled_does_not_stop_the_rest_of_the_pass()
    {
        using var root = ThreeProjectsWrittenOutOfOrder();
        var github = new FakeGitHub()
            .WithRepository(Alpha, "main", OpenIssue.Plain(1, "An issue on alpha", "A body."))
            .Failing(Mullet, "the API returned 503")
            .WithRepository(Zeta, "main", OpenIssue.Plain(3, "An issue on zeta", "A body."));
        await using var host = await FactoryHost.StartAsync(root, github: github);

        await host.PollAsync();

        // The broken repository took its turn like any other and the failure is contained
        // to it: the other two were still read, in the order the rotation came round in,
        // and their issues landed in Backlog.
        Assert.Equal([Alpha, Mullet, Zeta], github.Polled);
        Assert.Equal([1, 3], host.Store.List().Select(item => item.IssueNumber));

        // The next pass tries it again rather than writing it off. Whether a failure is
        // worth retrying sooner, and with what backoff, is the retry ticket's business;
        // what must not happen here is a repository being skipped for ever because it
        // misbehaved once. (Mullet's issue is created a pass later than the other two, so
        // the store's own ordering by creation puts it last.)
        github.WithRepository(Mullet, "main", OpenIssue.Plain(2, "An issue on mullet", "A body."));
        host.Clock.Advance(FactoryConstants.PollInterval);
        await host.PollAsync();

        Assert.Equal(2, github.TimesPolled(Mullet));
        Assert.Equal([1, 2, 3], host.Store.List().Select(item => item.IssueNumber).Order());
        Assert.Equal(Mullet, host.Store.List().Single(item => item.IssueNumber == 2).RepoUrl);
    }

    [Fact]
    public async Task The_same_issue_polled_twice_is_one_work_item_and_the_first_one_is_left_alone()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var github = new FakeGitHub().WithRepository(
            RepoUrl, defaultBranch: "main", OpenIssue.Plain(1, "One issue", "A body."));
        await using var host = await FactoryHost.StartAsync(root, github: github);

        await host.PollAsync();
        var first = Assert.Single(host.Store.List());

        // The work item is moved to Review with a round recorded against it, so a re-poll
        // has something worth disturbing if intake is going to disturb anything. The moves
        // are made through the store rather than by driving the loop, because what is
        // under test is that a second poll of an open issue leaves a record the loop has
        // already advanced exactly where it is.
        host.Store.Move(first.Id, Swimlane.Review);
        var round = host.Store.RecordRound(
            first.Id,
            RoundOutcome.Produced,
            "src/Index.cs +12 -3",
            "Added the endpoint.",
            host.Clock.UtcNow);

        github.WithRepository(
            RepoUrl, defaultBranch: "main", OpenIssue.Plain(1, "One issue", "A body."));
        host.Clock.Advance(FactoryConstants.PollInterval);
        await host.PollAsync();

        var after = Assert.Single(host.Store.List());

        Assert.Equal(first.Id, after.Id);
        Assert.Equal(Swimlane.Review, after.Swimlane);
        Assert.Equal(1, after.RoundCount);
        Assert.Equal(first.CreatedUtc, after.CreatedUtc);

        // The round the loop recorded is still the round it recorded, and the work item
        // is still where a reviewer left it — on the board, which is what a reviewer sees.
        var recorded = Assert.Single(host.Store.Rounds(after.Id));
        Assert.Equal(round.RoundNumber, recorded.RoundNumber);
        Assert.Equal("src/Index.cs +12 -3", recorded.ResultPayload);

        var board = await Board.ReadAsync(host.Board);
        Assert.Contains(first.Id.ToString(), board.Swimlane("Review"), StringComparison.Ordinal);
        Assert.DoesNotContain(first.Id.ToString(), board.Swimlane("Backlog"), StringComparison.Ordinal);

        // The repository was read twice. The second read found the issue still open, and
        // the store's answer to that was that it already had the work item.
        Assert.Equal(2, github.TimesPolled(RepoUrl));
    }

    [Fact]
    public async Task The_base_branch_is_the_one_the_repository_is_on_when_the_issue_is_polled()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var github = new FakeGitHub().WithRepository(
            RepoUrl, defaultBranch: "main", OpenIssue.Plain(1, "The first issue", "A body."));
        await using var host = await FactoryHost.StartAsync(root, github: github);

        await host.PollAsync();

        // The repository changes what it is built from, and a second issue opens. The
        // branch is read at poll time, so the new issue is built from the branch that is
        // current and the one already recorded keeps the branch it was created with.
        github.WithRepository(
            RepoUrl,
            defaultBranch: "release/2.0",
            OpenIssue.Plain(1, "The first issue", "A body."),
            OpenIssue.Plain(2, "The second issue", "A body."));
        host.Clock.Advance(FactoryConstants.PollInterval);
        await host.PollAsync();

        var first = host.Store.List().Single(item => item.IssueNumber == 1);
        var second = host.Store.List().Single(item => item.IssueNumber == 2);

        Assert.Equal("main", first.BaseBranch);
        Assert.Equal("release/2.0", second.BaseBranch);
        Assert.Equal(2, github.BranchesResolved(RepoUrl));
    }

    [Fact]
    public async Task Neither_a_label_nor_an_assignee_nor_anything_else_about_an_issue_decides_whether_it_is_intaken()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var github = new FakeGitHub().WithRepository(
            RepoUrl,
            defaultBranch: "main",
            // This repository's own AGENTS.md defines these five labels as a triage
            // vocabulary, and two of them mean the opposite of ready to build. They are
            // on the seam because GitHub issues have them, and a poller that filtered on
            // them would be reading a convention that belongs to one repository — which
            // is exactly what ADR-0007 refuses to do.
            new OpenIssue(10, "Ready for the agent", "A body.", ["ready-for-agent"], []),
            new OpenIssue(11, "Needs info before anything happens", "A body.", ["needs-info"], []),
            new OpenIssue(12, "Will not be fixed, but also ready", "A body.", ["wontfix", "ready-for-agent"], []),
            new OpenIssue(13, "Will not be fixed", "A body.", ["wontfix"], []),
            new OpenIssue(14, "Assigned to a human being", "A body.", [], ["dpven"]),
            OpenIssue.Plain(15, "Nothing on it at all", "A body."));
        await using var host = await FactoryHost.StartAsync(root, github: github);

        await host.PollAsync();

        // All six, in Backlog, with nothing to act on until a human decides from the board
        // that one of them is worth building. Which of these six is worth building is a
        // judgement about the repository, made by whoever reads them, not a rule the
        // factory applies on their behalf.
        var workItems = host.Store.List();
        Assert.Equal(6, workItems.Count);
        Assert.All(workItems, workItem => Assert.Equal(Swimlane.Backlog, workItem.Swimlane));
        Assert.Equal([10, 11, 12, 13, 14, 15], workItems.Select(workItem => workItem.IssueNumber).Order());

        var board = await Board.ReadAsync(host.Board);
        Assert.Contains("Will not be fixed", board.Swimlane("Backlog"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_project_file_that_was_refused_is_out_of_the_rotation_and_the_rest_still_run()
    {
        using var root = FactoryRoot.Create()
            .WithProjectFile("alpha.yaml", ProjectFile.For("mullet", Alpha))
            // A field the schema does not have. Its own project stays out of the rotation
            // and the factory still starts, so the rest of the rotation is unaffected.
            .WithProjectFile("broken.yaml", $"""
                name: broken
                repo:
                  url: {Zeta}
                  default_branch: main
                worker:
                  image: ghcr.io/nanisoft/agent-factory-worker:1
                llm:
                  provider: anthropic
                keys:
                  github: BROKEN_GITHUB_TOKEN
                  llm: BROKEN_ANTHROPIC_API_KEY
                """);
        var github = new FakeGitHub()
            .WithRepository(Alpha, "main", OpenIssue.Plain(1, "An issue on alpha", "A body."))
            .WithRepository(Zeta, "main", OpenIssue.Plain(2, "An issue on zeta", "A body."));
        await using var host = await FactoryHost.StartAsync(root, github: github);

        await host.PollAsync();

        Assert.Equal([Alpha], github.Polled);
        Assert.Equal("broken.yaml", Assert.Single(host.Projects.Rejections).FileName);
        Assert.Single(host.Store.List());
    }

    [Fact]
    public async Task A_factory_serving_no_projects_polls_nothing()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub();
        await using var host = await FactoryHost.StartAsync(root, github: github);

        Assert.False(await host.IntakeStepAsync());
        await host.PollAsync();

        Assert.Empty(github.Polled);
        Assert.Empty(host.Store.List());
    }

    [Fact]
    public void Intake_reads_the_world_only_through_the_one_github_seam()
    {
        // A structural check, and the only kind available for a claim about what code
        // does not reference. Intake's whole knowledge of the outside world is its
        // constructor: the store it records into, the one GitHub seam, the clock the
        // interval is measured against, the set of projects being served, a logger, and the
        // counters. A second boundary — a container runtime, a clock of its own, an HTTP
        // client that is not the seam — would have to arrive as an extra dependency, so the
        // list is the check. It is also what a timer would have to bypass to be one, since
        // the interval is a comparison against the clock the poller was handed.
        //
        // The sixth dependency is `FactoryMetrics` and it is not a sixth boundary: it holds
        // no clock, no transport, no credential reader, no store and no seam, and it
        // publishes over an in-process API rather than reaching anything. `PolicyTests`
        // asserts that list by hand and names this as the change it made.
        Assert.Equal(
            ["IWorkItemStore", "IGitHub", "IClock", "ProjectLoadReport", "ILogger`1", "FactoryMetrics"],
            typeof(Poller)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Select(parameter => parameter.ParameterType.Name));

        // One seam covering both halves of the boundary: intake reads through it and the
        // merger will write through it. Two seams would be two things that can disagree
        // about what a repository is, which is what one seam is for.
        Assert.Equal(
            ["GetDefaultBranchAsync", "ListOpenIssuesAsync", "OpenPullRequestAsync", "MergeAsync"],
            typeof(IGitHub).GetMethods().Select(method => method.Name));
    }

    /// <summary>
    /// Three projects whose files sort alpha, mullet, zeta, written in the reverse of
    /// that, so filesystem enumeration order cannot be what decided the rotation. The
    /// project names are shifted one place along from the files they sit in, so a
    /// rotation sorted by project name would come out in a different order entirely.
    /// </summary>
    private static FactoryRoot ThreeProjectsWrittenOutOfOrder() => FactoryRoot.Create()
        .WithProjectFile("zeta.yaml", ProjectFile.For("alpha", Zeta))
        .WithProjectFile("mullet.yaml", ProjectFile.For("zeta", Mullet))
        .WithProjectFile("alpha.yaml", ProjectFile.For("mullet", Alpha));
}
