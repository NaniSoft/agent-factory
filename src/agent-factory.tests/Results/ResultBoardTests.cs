namespace AgentFactory.Tests.Results;

using AgentFactory.Failures;
using AgentFactory.Results;
using AgentFactory.Rounds;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;

/// <summary>
/// A round's result on the board, over the real HTTP surface, with the agent faked. The
/// deriver's own tests prove what a result says; these prove a reviewer can reach it —
/// the files changed, the commands and what they returned, the diff, the agent's note, and
/// the log under all of it.
/// </summary>
public class ResultBoardTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";

    [Fact]
    public async Task A_reviewer_sees_the_files_changed_the_commands_and_the_diff()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundResult.Produced(
            ResultPayload.Of(Derived(), "round log line: the round's own output"),
            "Added the endpoint, because nothing was answering.",
            "round log line: the round's own output"));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(workItem.Id);

        await host.Settle();

        var review = (await Board.ReadAsync(host.Board)).Read("Review");

        // Story 24: which files changed, so a reviewer can judge the change.
        Assert.Contains("src/Index.cs", review, StringComparison.Ordinal);
        Assert.Contains("+12", review, StringComparison.Ordinal);

        // Story 25: which commands ran and what each returned.
        Assert.Contains("the unit tests", review, StringComparison.Ordinal);
        Assert.Contains("dotnet test", review, StringComparison.Ordinal);
        Assert.Contains("42 passed, 0 failed", review, StringComparison.Ordinal);

        // Story 26: the diff is what would ship, and the reviewer's account is visibly
        // separate from it.
        Assert.Contains("diff --git a/src/Index.cs", review, StringComparison.Ordinal);
        Assert.Contains("Added the endpoint, because nothing was answering.", review, StringComparison.Ordinal);
        Assert.Contains("nothing above is derived from", review, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rounds_log_is_on_the_board_where_a_reviewer_can_reach_it()
    {
        // Story 28, and the shape #3 could not offer: the log used to be streamed to the
        // factory's own logging and kept nowhere a reviewer could get at. It is now on the
        // round, and a reviewer reads it on the card the round is on.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundResult.Produced(
            ResultPayload.Of(Derived(), "log"),
            "A note.",
            "worker-round: cloning https://github.com/octocat/Hello-World.git @ master\nround log line: the round's own output"));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var promoted = host.Store.Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(promoted.Id);

        await host.Settle();

        var board = await Board.ReadAsync(host.Board);
        var review = board.Read("Review");

        Assert.Contains("worker-round: cloning", review, StringComparison.Ordinal);
        Assert.Contains("round log line", review, StringComparison.Ordinal);

        // The round is marked as carrying a log, so a reviewer knows there is something to
        // read on the card rather than having to go looking for it.
        Assert.Equal("true", board.Rendered("data-round", "1", "data-has-log"));
    }

    [Fact]
    public async Task A_round_that_produced_no_result_still_shows_a_reviewer_the_log()
    {
        // Story 29, and the case the old shape made impossible to satisfy at all: a round
        // with no payload used to render as an empty card, which reads as a round that
        // changed nothing rather than as a round that failed. The log is the whole of what
        // such a round has, and it is what the board shows.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundResult.Failed(
            FailureClass.Permanent,
            "worker-round: cloning\nfatal: could not read from the repository"));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(workItem.Id);

        await host.Settle();

        var board = await Board.ReadAsync(host.Board);
        var escalated = board.Read("Escalated");

        Assert.Equal(Swimlane.Escalated, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Contains("fatal: could not read from the repository", escalated, StringComparison.Ordinal);
        Assert.Contains("permanent failure", escalated, StringComparison.Ordinal);

        // The round is on the card, with no payload at all, and is marked as carrying a
        // log — so the only thing a round of this shape has is visibly there rather than
        // being an empty card.
        Assert.Equal("false", board.Rendered("data-round", "1", "data-has-result"));
        Assert.Equal("true", board.Rendered("data-round", "1", "data-has-log"));
    }

    [Fact]
    public async Task A_round_whose_result_could_not_be_read_says_so_on_the_board_rather_than_rendering_nothing()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundResult.Produced(
            "this round's result could not be read: its result file has no readable header\n\nthe round's log ends:\nfatal: the collector was killed",
            agentNote: null,
            log: "fatal: the collector was killed"));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var promoted2 = host.Store.Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(promoted2.Id);

        await host.Settle();

        var review = (await Board.ReadAsync(host.Board)).Read("Review");

        Assert.Contains("could not be read", review, StringComparison.Ordinal);
        Assert.Contains("fatal: the collector was killed", review, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_round_the_deriver_found_nothing_in_still_renders_something_rather_than_an_empty_card()
    {
        // A round that ran, changed nothing and passed its tests is a real and common
        // outcome. An empty `<pre>` would read as a round the factory failed to record, so
        // the payload says so in words.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Producing(ResultPayload.Of(
            Derived(
                files: [],
                commands: [
                    new CommandOutcome(1, "read the tree", "ls", "/work", 0, 5, "README\n", string.Empty, 7, 0),
                ],
                diff: string.Empty,
                note: null),
            log: "the round said it read the tree and stopped"));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var promoted3 = host.Store.Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(promoted3.Id);

        await host.Settle();

        var review = (await Board.ReadAsync(host.Board)).Read("Review");

        Assert.Contains("git saw the tree exactly as the round found it", review, StringComparison.Ordinal);
        Assert.Contains("read the tree", review, StringComparison.Ordinal);
        Assert.Contains("wrote none", review, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rounds_result_and_log_survive_a_restart_so_a_reviewer_can_still_read_them()
    {
        // Story 12 in the shape this ticket needs: the result is kept, and so is the log
        // under it. A board that lost the record of a round on restart would be a factory
        // that discarded an agent's work — which is what the store exists to prevent.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundResult.Produced(
            ResultPayload.Of(Derived(), "log"),
            "Added the endpoint.",
            "the round's log, which is also kept"));

        Guid workItemId;
        await using (var first = await FactoryHost.StartAsync(root, agent: agent))
        {
            workItemId = first.Store
                .Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem.Id;
            await first.PromoteAsync(workItemId);
            await first.Settle();
        }

        await using var restarted = await FactoryHost.StartAsync(root);

        var round = Assert.Single(restarted.Store.Rounds(workItemId));
        Assert.Contains("src/Index.cs", round.ResultPayload!, StringComparison.Ordinal);
        Assert.Equal("the round's log, which is also kept", round.Log);
        Assert.Equal("Added the endpoint.", round.AgentNote);

        var review = (await Board.ReadAsync(restarted.Board)).Read("Review");
        Assert.Contains("the round's log, which is also kept", review, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_board_says_which_credentials_the_round_was_handed_and_names_only()
    {
        // ADR-0006 as a reviewer sees it. The result carries the *names* the round was
        // handed, so a card can show what the agent had without a value ever travelling
        // into the store — and an empty list is checkable evidence rather than a claim.
        using var root = FactoryRoot.Create();
        var withKey = Derived(credentials: ["NEXUS_ANTHROPIC_API_KEY"]);
        var agent = new FakeNOpenCode().Producing(ResultPayload.Of(withKey));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var promoted4 = host.Store.Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(promoted4.Id);

        await host.Settle();
        Assert.Contains("NEXUS_ANTHROPIC_API_KEY", (await Board.ReadAsync(host.Board)).Read("Review"), StringComparison.Ordinal);

        // And a round that was handed nothing says none, which is the half a reviewer reads
        // to satisfy themselves the agent could not have pushed.
        using var second = FactoryRoot.Create();
        var bare = new FakeNOpenCode().Producing(ResultPayload.Of(Derived()));
        await using var without = await FactoryHost.StartAsync(second, agent: bare);
        var replaced = without.Store.Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await without.PromoteAsync(replaced.Id);

        await without.Settle();
        Assert.Contains(
            "credentials the round was handed: none",
            (await Board.ReadAsync(without.Board)).Read("Review"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A derived result shaped like a real one: one changed file, one passing and one
    /// failing command, a diff, and a note. Built here rather than read from a container so
    /// these tests say what the board renders without a Docker daemon anywhere.
    /// </summary>
    private static AgentFactory.Results.DerivedResult Derived(
        IReadOnlyList<AgentFactory.Results.ChangedFile>? files = null,
        IReadOnlyList<AgentFactory.Results.CommandOutcome>? commands = null,
        string diff = """
            diff --git a/src/Index.cs b/src/Index.cs
            index aaaaaaa..bbbbbbb 100644
            --- a/src/Index.cs
            +++ b/src/Index.cs
            @@ -1,3 +1,15 @@
            +public sealed class Index
            +{
            +}
            """,
        string? note = "Added the endpoint, because nothing was answering.",
        IReadOnlyList<string>? credentials = null) => new(
        files ?? [new AgentFactory.Results.ChangedFile("src/Index.cs", null, AgentFactory.Results.FileChange.Added, 12, 3, true, false)],
        commands ??
        [
            new AgentFactory.Results.CommandOutcome(1, "the unit tests", "dotnet test", "/work", 0, 1800, "42 passed, 0 failed\n", string.Empty, 21, 0),
            new AgentFactory.Results.CommandOutcome(2, "the linter", "dotnet format --verify-no-changes", "/work", 2, 900, string.Empty, "2 files need formatting\n", 0, 27),
        ],
        diff,
        DiffTruncated: false,
        note,
        [],
        new AgentFactory.Results.RoundEnvironment(
            User: "agent",
            Uid: 1000,
            IsARepository: true,
            Branch: "main",
            StartHead: "aaaaaaa1111111",
            Head: "bbbbbbb2222222",
            Remotes: string.Empty,
            CredentialNames: credentials ?? [],
            Git: "git version 2.43.0",
            Agent: "opencode v2.0.18",
            OperatingSystem: "Ubuntu 24.04"),
        UnreadableLines: 0,
        UnreadableBecause: null);
}
