namespace AgentFactory.Tests.WorkItems;

using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;

/// <summary>
/// Work items are the factory's own record of an issue, they are persisted, and the
/// board renders them. Driven through the running factory.
/// </summary>
public class WorkItemTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";

    private const string IssueBody = "What the issue says, in the maintainer's words.";

    [Fact]
    public async Task A_work_item_carries_its_project_its_source_issue_its_base_branch_and_its_swimlane()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        await using var host = await FactoryHost.StartAsync(root);

        var created = host.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main").WorkItem;

        var board = await Board.ReadAsync(host.Board);

        Assert.Equal(
            "nexus",
            board.Rendered("data-work-item", created.Id.ToString(), "data-project"));
        Assert.Equal("42", board.Rendered("data-work-item", created.Id.ToString(), "data-issue"));
        Assert.Equal("main", board.Rendered("data-work-item", created.Id.ToString(), "data-base-branch"));
        Assert.Equal(Swimlane.Backlog, created.Swimlane);
    }

    [Fact]
    public async Task The_board_shows_a_work_item_in_backlog()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        await using var host = await FactoryHost.StartAsync(root);

        var created = host.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main").WorkItem;

        var board = await Board.ReadAsync(host.Board);

        Assert.Contains(created.Id.ToString(), board.Swimlane("Backlog"), StringComparison.Ordinal);
        Assert.DoesNotContain(created.Id.ToString(), board.Swimlane("Frontier"), StringComparison.Ordinal);
        Assert.Contains("A work item, end to end", board.Swimlane("Backlog"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Backlog")]
    [InlineData("Frontier")]
    [InlineData("InProgress")]
    [InlineData("Review")]
    [InlineData("Done")]
    public async Task The_board_renders_every_swimlane(string swimlane)
    {
        using var root = FactoryRoot.Create();
        await using var host = await FactoryHost.StartAsync(root);

        var board = await Board.ReadAsync(host.Board);

        Assert.NotEqual(string.Empty, board.Swimlane(swimlane));
    }

    [Fact]
    public async Task The_board_renders_the_parked_and_final_swimlanes_too()
    {
        using var root = FactoryRoot.Create();
        await using var host = await FactoryHost.StartAsync(root);

        var board = await Board.ReadAsync(host.Board);

        // Escalated is parked rather than final; Rejected is final. Both are rendered,
        // because nothing ends in silence.
        Assert.NotEqual(string.Empty, board.Swimlane("Escalated"));
        Assert.NotEqual(string.Empty, board.Swimlane("Rejected"));
        Assert.True(board.Renders("In Progress"));
    }

    [Fact]
    public async Task Work_items_persist_in_sqlite_and_survive_a_restart()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);

        Guid id;
        await using (var first = await FactoryHost.StartAsync(root))
        {
            id = first.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main").WorkItem.Id;
        }

        Assert.True(File.Exists(root.DatabasePath), "the store is a SQLite file, not memory");

        await using var restarted = await FactoryHost.StartAsync(root);

        var board = await Board.ReadAsync(restarted.Board);

        Assert.Contains(id.ToString(), board.Swimlane("Backlog"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_restart_does_not_lose_the_base_branch_the_work_item_was_built_against()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);

        await using (var first = await FactoryHost.StartAsync(root))
        {
            first.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "release/2.0");
        }

        await using var restarted = await FactoryHost.StartAsync(root);

        Assert.Equal("release/2.0", restarted.Store.Get(
            restarted.Store.List().Single().Id)!.BaseBranch);
    }

    [Fact]
    public async Task A_work_item_is_timestamped_by_the_clock_the_factory_is_wired_to()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 4, 1, 8, 30, 0, TimeSpan.Zero));
        using var root = FactoryRoot.Create();
        await using var host = await FactoryHost.StartAsync(root, clock: clock);

        var first = host.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main").WorkItem;

        Assert.Equal(new DateTimeOffset(2026, 4, 1, 8, 30, 0, TimeSpan.Zero), first.CreatedUtc);

        clock.Advance(TimeSpan.FromMinutes(90));
        var second = host.Store.Intake("nexus", RepoUrl, 43, "A round, with the agent faked", IssueBody, "main").WorkItem;

        Assert.Equal(new DateTimeOffset(2026, 4, 1, 10, 0, 0, TimeSpan.Zero), second.CreatedUtc);
    }

    [Fact]
    public async Task One_issue_is_one_work_item()
    {
        using var root = FactoryRoot.Create();
        await using var host = await FactoryHost.StartAsync(root);

        var first = host.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main");

        // The store refuses the second record, which is what makes intake idempotent
        // without the poller having to remember what it has already seen. It answers
        // with the work item that is already there rather than throwing the database's
        // objection at the poller, so a re-poll is a no-op the poller can ignore.
        var second = host.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main");

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.WorkItem.Id, second.WorkItem.Id);
        Assert.Single(host.Store.List());
    }
}
