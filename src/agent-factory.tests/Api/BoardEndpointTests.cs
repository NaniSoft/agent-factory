namespace AgentFactory.Tests.Api;

using System.Net;
using System.Text.Json;
using AgentFactory;
using AgentFactory.Rounds;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;

/// <summary>
/// The first JSON endpoint, driven over the process's real HTTP surface. The factory, the
/// loop and the view model are the real thing; only the agent is substituted, and nothing
/// here waits. What is asserted is exactly what a renderer reads: the container budget as
/// the loop is counting it against the one code constant, and whether silence can merge.
/// </summary>
public class BoardEndpointTests
{
    private const string Nexus = "https://github.com/NaniSoft/nexus";
    private const string IssueBody = "What the issue says, in the maintainer's words.";

    [Fact]
    public async Task The_board_endpoint_reports_the_budget_and_the_auto_merge_mode()
    {
        using var root = FactoryRoot.Create();
        await using var host = await FactoryHost.StartAsync(root, autoMerge: true);

        using var response = await host.Board.GetAsync("/api/board");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var board = document.RootElement;

        // The budget is the real numbers: the loop's in-flight count, which is zero with an
        // idle machine, against the one code constant. Written out rather than read from the
        // constant, because a test that read it would prove only that the endpoint agrees
        // with itself.
        Assert.Equal(0, board.GetProperty("budget").GetProperty("inUse").GetInt32());
        Assert.Equal(2, board.GetProperty("budget").GetProperty("of").GetInt32());
        Assert.Equal(2, FactoryConstants.ContainerBudget);

        // The mode is the live option, not a default the endpoint chose.
        Assert.True(board.GetProperty("autoMerge").GetBoolean());
    }

    [Fact]
    public async Task The_budget_reads_a_round_the_loop_is_inside()
    {
        // The budget has to be the loop's own count and not a number the endpoint keeps, so
        // this holds two rounds open and reads the same endpoint the renderer reads. A copy
        // that never moved would return zero here and pass the test above.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Stuck().Stuck();
        await using var host = await FactoryHost.StartAsync(root, agent: agent);

        await Take(host, 42);
        await Take(host, 43);
        await host.Settle();

        Assert.Equal(2, host.RoundsInFlight);

        using var response = await host.Board.GetAsync("/api/board");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(2, document.RootElement.GetProperty("budget").GetProperty("inUse").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("budget").GetProperty("of").GetInt32());
    }

    [Fact]
    public async Task The_board_carries_the_lanes_the_cards_and_the_projects_it_serves()
    {
        using var root = FactoryRoot.Create()
            .WithProjectFile("nexus.yaml", ProjectFile.Valid)
            .WithRefusedFile("broken.yaml");
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);

        var workItem = host.Store
            .Intake("nexus", Nexus, 42, "A work item, end to end", IssueBody, "main")
            .WorkItem;
        await host.PromoteAsync(workItem.Id);
        await host.Settle();
        Assert.Equal(Swimlane.Review, host.Store.Get(workItem.Id)!.Swimlane);

        using var response = await host.Board.GetAsync("/api/board");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var board = document.RootElement;

        // The lanes are the board's own set, in the board's own order: the five swimlanes
        // and then the two endings. Written out rather than read from the constant, because
        // a test that read it would prove only that the endpoint agrees with itself.
        var lanes = board.GetProperty("lanes").EnumerateArray().ToList();
        Assert.Equal(
            ["Backlog", "Frontier", "InProgress", "Review", "Done", "Escalated", "Rejected"],
            [.. lanes.Select(lane => lane.GetProperty("lane").GetString()!)]);

        // The lane's words come from the factory's own label, so "InProgress" reads as
        // "In Progress" without the renderer knowing the mapping.
        var review = lanes.Single(lane => lane.GetProperty("lane").GetString() == "Review");
        Assert.Equal("Review", review.GetProperty("label").GetString());
        Assert.Equal(
            "In Progress",
            lanes.Single(lane => lane.GetProperty("lane").GetString() == "InProgress").GetProperty("label").GetString());

        // The one work item, in Review, with every field a card reads.
        var card = Assert.Single(review.GetProperty("cards").EnumerateArray());
        Assert.Equal(workItem.Id.ToString(), card.GetProperty("id").GetString());
        Assert.Equal("nexus", card.GetProperty("project").GetString());
        Assert.Equal(42, card.GetProperty("issueNumber").GetInt32());
        Assert.Equal("A work item, end to end", card.GetProperty("title").GetString());
        Assert.Equal("Review", card.GetProperty("lane").GetString());
        Assert.Equal("Review", card.GetProperty("laneLabel").GetString());
        Assert.Equal(1, card.GetProperty("roundCount").GetInt32());
        Assert.Equal(3, card.GetProperty("roundCeiling").GetInt32());

        // A stage is not an ending, so the ending is empty here; and Review offers all three
        // decisions, in the order the board offers them. The decision form is a later
        // ticket's, but the offered set is the factory's judgement and belongs on the card.
        Assert.Equal(string.Empty, card.GetProperty("ending").GetString());
        Assert.Equal(
            ["approve", "request-changes", "reject"],
            [.. card.GetProperty("decisions").EnumerateArray().Select(decision => decision.GetString()!)]);

        // Every other lane is present and empty.
        Assert.All(
            lanes.Where(lane => lane.GetProperty("lane").GetString() != "Review"),
            lane => Assert.Empty(lane.GetProperty("cards").EnumerateArray()));

        // The served projects and the refused files travel with the board, so the filter and
        // the later tickets read them here rather than from a second endpoint.
        var project = Assert.Single(board.GetProperty("projects").EnumerateArray());
        Assert.Equal("nexus", project.GetProperty("name").GetString());
        Assert.Equal(Nexus, project.GetProperty("repoUrl").GetString());
        Assert.Equal(ProjectFile.Model, project.GetProperty("llmModel").GetString());

        var rejection = Assert.Single(board.GetProperty("rejections").EnumerateArray());
        Assert.Equal("broken.yaml", rejection.GetProperty("fileName").GetString());
        Assert.Contains(
            rejection.GetProperty("reason").GetString(),
            new[] { "Partial", "Shared", "Included", "Generated", "Invalid" });
        Assert.False(string.IsNullOrWhiteSpace(rejection.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task A_card_in_an_ending_lane_carries_why_it_ended()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);

        var workItem = host.Store
            .Intake("nexus", Nexus, 42, "A work item, end to end", IssueBody, "main")
            .WorkItem;
        await host.PromoteAsync(workItem.Id);
        await host.Settle();

        // A reviewer declines it, the loop applies the decision, and the card lands in
        // Rejected carrying the factory's own words for why.
        host.Store.RecordDecision(workItem.Id, Decision.Reject, null);
        await host.Settle();

        using var response = await host.Board.GetAsync("/api/board");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var lanes = document.RootElement.GetProperty("lanes").EnumerateArray().ToList();

        var rejected = lanes.Single(lane => lane.GetProperty("lane").GetString() == "Rejected");
        var card = Assert.Single(rejected.GetProperty("cards").EnumerateArray());
        Assert.Contains("Rejected", card.GetProperty("ending").GetString(), StringComparison.Ordinal);
        Assert.Equal("Rejected", card.GetProperty("laneLabel").GetString());

        // A declined work item is final, so the factory offers no decision on it.
        Assert.Empty(card.GetProperty("decisions").EnumerateArray());
    }

    [Fact]
    public async Task A_review_card_carries_when_silence_would_merge_it_and_only_when_auto_merge_is_on()
    {
        // The countdown is the factory's own field, present only for a card in Review while
        // auto-merge is on: it is ReviewStartedUtc + the feedback threshold, and with the
        // mode off there is nothing a clock could say, so it is null.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "Added the endpoint.");

        await using (var on = await FactoryHost.StartAsync(root, agent: agent, autoMerge: true))
        {
            var workItem = on.Store
                .Intake("nexus", Nexus, 42, "A work item, end to end", IssueBody, "main")
                .WorkItem;
            await on.PromoteAsync(workItem.Id);
            await on.Settle();

            var since = on.Store.Get(workItem.Id)!.ReviewStartedUtc!.Value;
            using var response = await on.Board.GetAsync("/api/board");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var card = CardIn(document.RootElement, "Review");

            Assert.Equal(
                since + FactoryConstants.FeedbackThreshold,
                DateTimeOffset.Parse(
                    card.GetProperty("autoMergeAt").GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind));
        }

        using var offRoot = FactoryRoot.Create();
        var offAgent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "Added the endpoint.");
        await using var off = await FactoryHost.StartAsync(offRoot, agent: offAgent, autoMerge: false);
        var waiting = off.Store
            .Intake("nexus", Nexus, 42, "A work item, end to end", IssueBody, "main")
            .WorkItem;
        await off.PromoteAsync(waiting.Id);
        await off.Settle();

        using var offResponse = await off.Board.GetAsync("/api/board");
        using var offDocument = JsonDocument.Parse(await offResponse.Content.ReadAsStringAsync());
        var offCard = CardIn(offDocument.RootElement, "Review");

        Assert.Equal(JsonValueKind.Null, offCard.GetProperty("autoMergeAt").ValueKind);
    }

    [Fact]
    public async Task The_filter_offers_a_project_with_work_items_that_is_no_longer_served()
    {
        // The served set is the projects the factory is serving; a project file that has
        // since been removed still has work items a reviewer is judging, and the filter has
        // to reach them. The board's `projects` stays the served set; `filterProjects` is the
        // union the filter offers.
        using var root = FactoryRoot.Create();
        await using var host = await FactoryHost.StartAsync(root);

        host.Store.Intake("atlas", "https://github.com/NaniSoft/atlas", 7, "Leftover work", IssueBody, "main");

        using var response = await host.Board.GetAsync("/api/board");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var board = document.RootElement;

        Assert.Empty(board.GetProperty("projects").EnumerateArray());
        Assert.Contains(
            "atlas",
            board.GetProperty("filterProjects").EnumerateArray().Select(name => name.GetString()));
    }

    /// <summary>The one card standing in a lane, from a board read.</summary>
    private static JsonElement CardIn(JsonElement board, string lane) =>
        Assert.Single(
            board.GetProperty("lanes").EnumerateArray()
                .Single(item => item.GetProperty("lane").GetString() == lane)
                .GetProperty("cards")
                .EnumerateArray());

    private static async Task<WorkItem> Take(FactoryHost host, int issueNumber)
    {
        var workItem = host.Store
            .Intake("nexus", Nexus, issueNumber, $"Issue {issueNumber}", IssueBody, "main")
            .WorkItem;
        await host.PromoteAsync(workItem.Id);
        return workItem;
    }
}
