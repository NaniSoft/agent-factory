namespace AgentFactory.Tests.Api;

using System.Text.Json;
using AgentFactory;
using AgentFactory.Failures;
using AgentFactory.GitHub;
using AgentFactory.Tests.Boundary;

/// <summary>
/// The Board endpoint's account of intake, driven over the process's real HTTP surface.
/// The factory, the poller, the loop and the view model are the real thing; only the
/// GitHub seam is substituted, and only because intake reads through it.
/// </summary>
/// <remarks>
/// <para>
/// This is the JSON half of what <c>IntakeBoardTests</c> asserts: a
/// project whose intake failed has produced no work item, so the only place its fault can
/// live is the intake section above the lanes. An empty Backlog is three different facts —
/// read and nothing open, never read, or read and refused — and the renderer re-decides
/// none of them: the three states and their sentences are the factory's own judgement,
/// serialised.
/// </para>
/// <para>
/// The whole of <c>HowToReadIntake</c> is reused rather than re-expressed here, so a state
/// the board can name and a state the JSON can carry are the same state by construction.
/// </para>
/// </remarks>
public class IntakeEndpointTests
{
    private const string Nexus = "https://github.com/NaniSoft/nexus";
    private const string Alpha = "https://github.com/NaniSoft/alpha";
    private const string Zeta = "https://github.com/NaniSoft/zeta";

    [Fact]
    public async Task The_board_carries_each_projects_intake_state()
    {
        // Three projects and two steps of the rotation is how all three states land at
        // once: alpha is read and has nothing open, nexus fails permanently, and zeta's
        // turn has not come round yet.
        using var root = FactoryRoot.Create()
            .WithProjectFile("alpha.yaml", ProjectFile.For("alpha", Alpha))
            .WithProjectFile("nexus.yaml", ProjectFile.For("nexus", Nexus))
            .WithProjectFile("zeta.yaml", ProjectFile.For("zeta", Zeta));

        var github = new FakeGitHub()
            .WithRepository(Alpha, defaultBranch: "main")
            .Failing(Nexus, FailureClass.Permanent, "the repository does not exist")
            .WithRepository(Zeta, defaultBranch: "main");
        await using var host = await FactoryHost.StartAsync(root, github: github);

        Assert.True(await host.IntakeStepAsync());
        Assert.True(await host.IntakeStepAsync());

        var intake = await ReadIntakeAsync(host);

        // The section's own state is the worst of the projects', so a renderer can say
        // "something here is not working" without reading every row to find out which.
        Assert.Equal("failing", intake.GetProperty("status").GetString());
        Assert.Equal(3, intake.GetProperty("projects").GetInt32());
        Assert.Equal(1, intake.GetProperty("polled").GetInt32());
        Assert.Equal(1, intake.GetProperty("neverPolled").GetInt32());
        Assert.Equal(1, intake.GetProperty("failing").GetInt32());

        // One row per served project, in the poller's rotation order, each naming its own
        // state. The three states are distinct values in the JSON, not three sentences a
        // reader has to tell apart.
        var rows = intake.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(["alpha", "nexus", "zeta"], [.. rows.Select(row => row.GetProperty("project").GetString()!)]);
        Assert.Equal("polled", Row(rows, "alpha").GetProperty("status").GetString());
        Assert.Equal("failing", Row(rows, "nexus").GetProperty("status").GetString());
        Assert.Equal("never-polled", Row(rows, "zeta").GetProperty("status").GetString());

        // A polled row says what it found: zero is a real answer and is carried as one.
        Assert.Equal(0, Row(rows, "alpha").GetProperty("openIssues").GetInt32());

        // The repository travels on the row, so the row says which one it is about.
        Assert.Equal(Nexus, Row(rows, "nexus").GetProperty("repoUrl").GetString());
    }

    [Fact]
    public async Task A_polled_row_carries_the_number_of_open_issues_it_found()
    {
        // The same state as alpha above with something to report, so the count on a polled
        // row is a number the renderer reads rather than a constant it composes.
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var github = new FakeGitHub().WithRepository(
            Nexus,
            defaultBranch: "main",
            OpenIssue.Plain(1, "The first issue", "A body."),
            OpenIssue.Plain(2, "The second issue", "A body."));
        await using var host = await FactoryHost.StartAsync(root, github: github);

        await host.PollAsync();

        var intake = await ReadIntakeAsync(host);
        var row = Assert.Single(intake.GetProperty("rows").EnumerateArray());

        Assert.Equal("polled", row.GetProperty("status").GetString());
        Assert.Equal(2, row.GetProperty("openIssues").GetInt32());
        Assert.Contains("2 open issue(s)", row.GetProperty("says").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_projects_row_says_its_classification_and_that_it_will_never_be_asked_again()
    {
        // A permanent failure is not asked again at all, and "never" is the one a renderer
        // has to be able to show without reading a sentence — a project left failing for
        // ever is not a project that will fix itself.
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var github = new FakeGitHub().Failing(Nexus, FailureClass.Permanent, "the repository does not exist");
        await using var host = await FactoryHost.StartAsync(root, github: github);

        await host.PollAsync();

        var intake = await ReadIntakeAsync(host);
        var row = Assert.Single(intake.GetProperty("rows").EnumerateArray());

        Assert.Equal("failing", row.GetProperty("status").GetString());
        Assert.Equal("Permanent", row.GetProperty("failure").GetString());
        Assert.Equal(1, row.GetProperty("failures").GetInt32());
        Assert.Equal("never", row.GetProperty("again").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("againAfterUtc").ValueKind);

        // A read that did not happen claims no count, and GitHub's own words are carried so
        // the operator knows which fault it is.
        Assert.Equal(JsonValueKind.Null, row.GetProperty("openIssues").ValueKind);
        Assert.Contains("the repository does not exist", row.GetProperty("because").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_transient_intake_failure_says_when_the_project_will_be_read_again()
    {
        // "Failing" and "failing and we are still asking" are different situations: one
        // wants a restart, the other patience. The moment is carried as the clock it was
        // measured against rather than as the backoff constant, so the row says when this
        // project is actually next read.
        var clock = new TestClock();
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var github = new FakeGitHub().Failing(Nexus, FailureClass.Transient, "the API returned 503");
        await using var host = await FactoryHost.StartAsync(root, clock, github: github);

        await host.PollAsync();

        var intake = await ReadIntakeAsync(host);
        var row = Assert.Single(intake.GetProperty("rows").EnumerateArray());

        Assert.Equal("failing", row.GetProperty("status").GetString());
        Assert.Equal("Transient", row.GetProperty("failure").GetString());
        Assert.Equal(
            (clock.UtcNow + FactoryConstants.PollBackoff(1)).ToString("O"),
            row.GetProperty("again").GetString());
        Assert.Equal(
            (clock.UtcNow + FactoryConstants.PollBackoff(1)).ToString("O"),
            row.GetProperty("againAfterUtc").GetString());
    }

    [Fact]
    public async Task An_empty_backlog_while_intake_is_broken_cannot_be_read_as_nothing_to_do()
    {
        // **The JSON half of the test that would have caught #16.** Two projects, a healthy
        // one with nothing open and one that cannot be read at all, and so an empty Backlog
        // — the board the first real run produced and could not be trusted. A renderer that
        // read only the lanes would draw a perfect empty board.
        using var root = FactoryRoot.Create()
            .WithProjectFile("alpha.yaml", ProjectFile.For("alpha", Alpha))
            .WithProjectFile("nexus.yaml", ProjectFile.For("nexus", Nexus));

        var github = new FakeGitHub()
            .WithRepository(Alpha, defaultBranch: "main")
            .Failing(Nexus, FailureClass.Permanent, "403: your credential cannot read this repository");
        await using var host = await FactoryHost.StartAsync(root, github: github);

        await host.PollAsync();

        // The precondition: the board really is empty.
        Assert.Equal(1, github.TimesPolled(Nexus));
        Assert.Equal(1, github.TimesPolled(Alpha));
        Assert.Empty(host.Store.List());

        using var response = await host.Board.GetAsync("/api/board");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var board = document.RootElement;

        var backlog = board.GetProperty("lanes").EnumerateArray()
            .Single(lane => lane.GetProperty("lane").GetString() == "Backlog");
        Assert.Empty(backlog.GetProperty("cards").EnumerateArray());

        // And the fault is on the same response, above the lanes, naming the project. The
        // sentence that ties the two together is the factory's own, so the renderer draws
        // it rather than deciding when to say it.
        var intake = board.GetProperty("intake");
        Assert.Equal("failing", intake.GetProperty("status").GetString());
        Assert.Contains(
            "An empty Backlog does not mean there is nothing to do while intake is broken",
            intake.GetProperty("summary").GetString(),
            StringComparison.Ordinal);

        var rows = intake.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal("failing", Row(rows, "nexus").GetProperty("status").GetString());
        Assert.Equal("polled", Row(rows, "alpha").GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_board_carries_an_intake_section_with_no_projects_rather_than_omitting_it()
    {
        // A factory serving no projects still answers the intake question, so a renderer
        // reads an empty section rather than a missing one. An absent key and an empty
        // section are the same to a careful reader and different to a careless one.
        using var root = FactoryRoot.Create();
        await using var host = await FactoryHost.StartAsync(root);

        var intake = await ReadIntakeAsync(host);

        Assert.Equal(0, intake.GetProperty("projects").GetInt32());
        Assert.Empty(intake.GetProperty("rows").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(intake.GetProperty("summary").GetString()));
    }

    private static async Task<JsonElement> ReadIntakeAsync(FactoryHost host)
    {
        using var response = await host.Board.GetAsync("/api/board");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("intake").Clone();
    }

    private static JsonElement Row(IReadOnlyList<JsonElement> rows, string project) =>
        rows.Single(row => row.GetProperty("project").GetString() == project);
}
