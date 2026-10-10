namespace AgentFactory.Tests.Api;

using System.Net;
using System.Text.Json;
using AgentFactory;
using AgentFactory.Failures;
using AgentFactory.Results;
using AgentFactory.Rounds;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;

/// <summary>
/// The work-item detail endpoint, driven over the process's real HTTP surface. The factory,
/// the loop, the store and the view model are the real thing; only the agent is
/// substituted, and nothing here waits. What is asserted is exactly what the detail page
/// reads: the work item's header, every round with the host's diff and its four states told
/// apart, the decisions made and offered, how it ended, and its route.
/// </summary>
public class WorkItemEndpointTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";

    [Fact]
    public async Task The_detail_endpoint_reports_the_work_item_its_rounds_its_decisions_and_its_ending()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundResult.Produced(
            "the container recorded this about itself",
            "Added the endpoint.",
            "the round's log",
            AChangeTo("src/Index.cs")));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "A work item, end to end", "What the issue says.", "main")
            .WorkItem;
        await host.PromoteAsync(workItem.Id);
        await host.Settle();

        // In Review: the header reads from the store, one round came back with its change,
        // and every one of the three decisions is offered.
        var review = await Detail(host, workItem.Id);

        var header = review.GetProperty("workItem");
        Assert.Equal(workItem.Id.ToString(), header.GetProperty("id").GetString());
        Assert.Equal("nexus", header.GetProperty("project").GetString());
        Assert.Equal(RepoUrl, header.GetProperty("repoUrl").GetString());
        Assert.Equal(42, header.GetProperty("issueNumber").GetInt32());
        Assert.Equal("A work item, end to end", header.GetProperty("title").GetString());
        Assert.Equal("main", header.GetProperty("baseBranch").GetString());
        Assert.Equal("Review", header.GetProperty("lane").GetString());
        Assert.Equal("Review", header.GetProperty("laneLabel").GetString());
        Assert.Equal(1, header.GetProperty("roundCount").GetInt32());
        Assert.Equal(3, header.GetProperty("roundCeiling").GetInt32());
        Assert.Equal("Unrouted", header.GetProperty("kind").GetString());

        var round = Assert.Single(review.GetProperty("rounds").EnumerateArray());
        Assert.Equal(1, round.GetProperty("roundNumber").GetInt32());
        Assert.Equal("Produced", round.GetProperty("outcome").GetString());
        Assert.Equal("the round's log", round.GetProperty("log").GetString());
        Assert.Equal("Added the endpoint.", round.GetProperty("agentNote").GetString());
        Assert.Contains("the container recorded this", round.GetProperty("payload").GetString()!, StringComparison.Ordinal);

        // The change, with git's own text for the file the round wrote.
        var diff = round.GetProperty("diff");
        Assert.Equal("shown", diff.GetProperty("state").GetString());
        var file = Assert.Single(diff.GetProperty("files").EnumerateArray());
        Assert.Equal("src/Index.cs", file.GetProperty("path").GetString());
        Assert.Equal("added", file.GetProperty("change").GetString());
        Assert.Equal(2, file.GetProperty("added").GetInt32());
        Assert.True(file.GetProperty("open").GetBoolean(), "the first file is open without a click");
        Assert.Contains("diff --git a/src/Index.cs", file.GetProperty("text").GetString()!, StringComparison.Ordinal);

        // Offered decisions are the factory's own set for the lane, in its own order.
        Assert.Equal(
            ["approve", "request-changes", "reject"],
            [.. review.GetProperty("offeredDecisions").EnumerateArray().Select(slug => slug.GetString()!)]);

        // A stage is not an ending.
        Assert.Equal(string.Empty, review.GetProperty("ending").GetString());

        // The route is the factory's judgement, which is honestly "nobody has looked" here.
        var route = review.GetProperty("route");
        Assert.Equal("Unrouted", route.GetProperty("kind").GetString());
        Assert.Contains("Nobody has looked at this issue yet", route.GetProperty("say").GetString()!, StringComparison.Ordinal);
        Assert.False(route.GetProperty("offersAcceptance").GetBoolean());

        // A reviewer declines it, and the record of that decision and the ending both show.
        host.Store.RecordDecision(workItem.Id, Decision.Reject, null);
        await host.Settle();

        var rejected = await Detail(host, workItem.Id);
        var decision = Assert.Single(rejected.GetProperty("decisions").EnumerateArray());
        Assert.Equal(1, decision.GetProperty("sequence").GetInt32());
        Assert.Equal("reject", decision.GetProperty("decision").GetString());
        Assert.Equal("Rejected", decision.GetProperty("appliedTo").GetString());
        Assert.Equal("Rejected", decision.GetProperty("appliedToLabel").GetString());
        Assert.Contains("Rejected", rejected.GetProperty("ending").GetString()!, StringComparison.Ordinal);

        // A declined work item is final, so nothing is offered on it.
        Assert.Empty(rejected.GetProperty("offeredDecisions").EnumerateArray());
    }

    [Fact]
    public async Task A_round_that_did_not_finish_is_told_apart_from_a_round_that_changed_nothing()
    {
        // The two states differ only in the round's outcome over two byte-identical empty
        // diffs. If the endpoint collapsed them, a rate-limited round would read as one that
        // chose to change nothing — the false claim #22 was about.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode()
            .Yielding(RoundResult.Produced(
                "the round read the tree and stopped",
                "Read the tree and stopped.",
                "the round read the tree and stopped",
                NoChange()))
            .Yielding(RoundResult.Failed(
                FailureClass.Permanent,
                "worker-round: roundExitCode=1",
                NoChange(),
                "outcome THE ROUND'S OWN COMMAND EXITED 1"));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main")
            .WorkItem;
        await host.PromoteAsync(workItem.Id);
        await host.Settle();

        var first = await Detail(host, workItem.Id);
        var empty = Assert.Single(first.GetProperty("rounds").EnumerateArray()).GetProperty("diff");
        Assert.Equal("empty", empty.GetProperty("state").GetString());
        Assert.Contains("Empty", empty.GetProperty("saysAboutAnUnchangedDisk").GetString()!, StringComparison.Ordinal);

        // A second round, which did not run to completion, over the same unchanged disk.
        host.Store.RecordDecision(workItem.Id, Decision.RequestChanges, "Try it again.");
        await host.Settle();

        var second = await Detail(host, workItem.Id);
        var rounds = second.GetProperty("rounds").EnumerateArray().ToList();
        Assert.Equal(2, rounds.Count);

        var unfinished = rounds[1].GetProperty("diff");
        Assert.Equal("unfinished", unfinished.GetProperty("state").GetString());
        Assert.Contains("Unfinished", unfinished.GetProperty("saysAboutAnUnchangedDisk").GetString()!, StringComparison.Ordinal);
        Assert.Contains("did not run to completion", unfinished.GetProperty("saysAboutAnUnchangedDisk").GetString()!, StringComparison.Ordinal);
        Assert.DoesNotContain("Empty. Nothing on disk differs", unfinished.GetProperty("saysAboutAnUnchangedDisk").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bounded_diff_says_how_much_was_left_off_and_points_at_the_tree()
    {
        using var root = FactoryRoot.Create();
        var many = Enumerable.Range(1, 205).Select(n => $"src/Generated/File{n:0000}.cs").ToArray();
        var agent = new FakeNOpenCode().Yielding(RoundResult.Produced(
            "the container recorded this about itself",
            "Regenerated the client.",
            "the round's log",
            AChangeTo(many)));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main")
            .WorkItem;
        await host.PromoteAsync(workItem.Id);
        await host.Settle();

        var diff = Assert.Single((await Detail(host, workItem.Id)).GetProperty("rounds").EnumerateArray())
            .GetProperty("diff");

        Assert.Equal("shown", diff.GetProperty("state").GetString());
        Assert.Equal(205, diff.GetProperty("totalFiles").GetInt32());
        Assert.True(diff.GetProperty("omittedFiles").GetInt32() > 0, "a 205-file diff should not all fit on one page");
        Assert.Equal(
            diff.GetProperty("totalFiles").GetInt32(),
            diff.GetProperty("files").GetArrayLength() + diff.GetProperty("omittedFiles").GetInt32());

        // What was left off is counted and the tree is named, so the renderer says both
        // rather than showing a smaller change that looks whole.
        var tree = diff.GetProperty("tree").GetString()!;
        Assert.NotEmpty(tree);
        var leftOff = diff.GetProperty("whatIsLeftOff").GetString()!;
        Assert.Contains("not on this page", leftOff, StringComparison.Ordinal);
        Assert.Contains(tree, leftOff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_work_item_is_not_found_rather_than_an_empty_record()
    {
        using var root = FactoryRoot.Create();
        await using var host = await FactoryHost.StartAsync(root);

        using var unknown = await host.Board.GetAsync($"/api/work-items/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        using var malformed = await host.Board.GetAsync("/api/work-items/not-a-guid");
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<JsonElement> Detail(FactoryHost host, Guid workItemId)
    {
        using var response = await host.Board.GetAsync($"/api/work-items/{workItemId:D}");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>A host diff of a change to the named files, as <c>git diff</c> would write it.</summary>
    private static HostDiff AChangeTo(params string[] paths)
    {
        var text = string.Join(
            "\n",
            paths.Select(path => string.Join(
                "\n",
                [
                    $"diff --git a/{path} b/{path}",
                    "new file mode 100644",
                    "index 0000000..1111111",
                    "--- /dev/null",
                    $"+++ b/{path}",
                    "@@ -0,0 +1,3 @@",
                    $"+public sealed class {Path.GetFileNameWithoutExtension(path)} {{ }}",
                    "+",
                ])));

        return new HostDiff(text, string.Empty, $"/rounds/{Guid.Empty:N}/tree", ContainerBounded: false, UnavailableBecause: null);
    }

    /// <summary>A diff of an unchanged disk: empty text, and a tree the round left.</summary>
    private static HostDiff NoChange() =>
        new(string.Empty, string.Empty, "/rounds/abc/tree", ContainerBounded: false, UnavailableBecause: null);
}
