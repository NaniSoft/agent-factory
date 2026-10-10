namespace AgentFactory.Tests.Api;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentFactory.Rounds;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;

/// <summary>
/// The decisions endpoint, driven over the process's real HTTP surface. It is the JSON
/// twin of the board's own form handler: the store, the loop and the view model are the
/// real thing; only the agent, the clock and GitHub are substituted, and nothing here
/// waits. What is asserted is exactly what a renderer reads and posts — whether the loop
/// applied the decision, the factory's own words when it did not, and the lane the work
/// item is in afterwards.
/// </summary>
public class DecisionsEndpointTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";
    private const string IssueBody = "What the issue says, in the maintainer's words.";
    private const string Feedback = "The null check is outside the lock; move it inside.";

    // The store's own two refusals, written out rather than read from the store, because a
    // test that read them would prove only that the endpoint and the store agree by
    // construction. These are the words a reviewer has already read on the board.
    private const string NeedsWords =
        "requesting changes needs the reviewer's reasons: they are the next round's brief, "
            + "and there is nothing here to hand it";

    private const string NotADecision =
        "That is not one of the three decisions. A work item in Review is approved, sent "
            + "back for changes, or rejected, and there is nothing else to decide.";

    [Fact]
    public async Task An_approve_on_a_review_item_records_steps_and_returns_the_lane()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().Merging();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github);
        var workItem = await InReview(host);

        using var response = await host.Board.PostAsJsonAsync(
            $"/api/work-items/{workItem.Id}/decisions",
            new { decision = "approve", feedback = Feedback });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Read(response);

        // The loop carried it out, so there is nothing to refuse and the lane the renderer
        // reads is the factory's own answer: Done, because a merge landed.
        Assert.True(result.Applied);
        Assert.Null(result.Refusal);
        Assert.Equal("Done", result.ResultingLane);

        // The merge was asked for through the one seam, and the decision is on the record
        // marked as what applied it — done means merged, and the record says so.
        Assert.Equal([new MergeAttempt(RepoUrl, 42)], github.Merges);
        Assert.Equal(Swimlane.Done, host.Store.Get(workItem.Id)!.Swimlane);
        var recorded = Assert.Single(host.Store.Decisions(workItem.Id));
        Assert.Equal(Decision.Approve, recorded.Decision);
        Assert.Equal(Swimlane.Done, recorded.AppliedTo);
    }

    [Fact]
    public async Task An_approval_the_merge_could_not_carry_out_is_refused_and_says_where_it_went()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github);
        var workItem = await InReview(host);

        using var response = await host.Board.PostAsJsonAsync(
            $"/api/work-items/{workItem.Id}/decisions",
            new { decision = "approve", feedback = (string?)null });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Read(response);

        // Not applied, and the loop's own refusal travels on the response the reviewer is
        // holding rather than being lost to the next board read.
        Assert.False(result.Applied);
        Assert.Contains("was approved, but the change was not merged", result.Refusal, StringComparison.Ordinal);
        Assert.Contains("nothing shipped", result.Refusal, StringComparison.Ordinal);
        Assert.Equal("Escalated", result.ResultingLane);
        Assert.Equal(Swimlane.Escalated, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Equal(Swimlane.Escalated, Assert.Single(host.Store.Decisions(workItem.Id)).AppliedTo);
    }

    [Fact]
    public async Task Request_changes_with_no_words_is_refused_and_briefs_no_round()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = await InReview(host);

        // Empty and blank are the same absence of words, and both are refused with the
        // store's own message. A refusal means nothing happened: the work item is still the
        // reviewer's to decide, no decision is on the record, and no round was briefed.
        foreach (var silent in new[] { "", "   " })
        {
            using var response = await host.Board.PostAsJsonAsync(
                $"/api/work-items/{workItem.Id}/decisions",
                new { decision = "request-changes", feedback = silent });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await Read(response);
            Assert.False(result.Applied);
            Assert.Equal(NeedsWords, result.Refusal);
            Assert.Equal("Review", result.ResultingLane);
        }

        Assert.Equal(Swimlane.Review, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Empty(host.Store.Decisions(workItem.Id));
        Assert.Single(agent.AskedFor);
    }

    [Theory]
    [InlineData("shelve")]
    [InlineData("merge")]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_fourth_or_unknown_decision_is_refused_rather_than_guessed_at(string? decision)
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().Merging();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github);
        var workItem = await InReview(host);

        using var response = await host.Board.PostAsJsonAsync(
            $"/api/work-items/{workItem.Id}/decisions",
            new { decision, feedback = Feedback });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Read(response);

        // Refused, so nothing is merged and nothing is recorded: the loop cannot be talked
        // into a decision by a value the board did not offer a reviewer.
        Assert.False(result.Applied);
        Assert.Equal(NotADecision, result.Refusal);
        Assert.Equal("Review", result.ResultingLane);
        Assert.Equal(Swimlane.Review, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Empty(host.Store.Decisions(workItem.Id));
        Assert.Empty(github.Merges);
    }

    [Fact]
    public async Task A_decision_on_a_lane_a_reviewer_cannot_act_on_is_refused()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);

        // A work item in Backlog: intake has made it and nobody has accepted it into the
        // build, so it is not the reviewer's to decide about yet.
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main")
            .WorkItem;
        Assert.Equal(Swimlane.Backlog, host.Store.Get(workItem.Id)!.Swimlane);

        using var response = await host.Board.PostAsJsonAsync(
            $"/api/work-items/{workItem.Id}/decisions",
            new { decision = "approve", feedback = (string?)null });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await Read(response);

        // The store's own words, and the work item is exactly where it was. Nothing is
        // merged and nothing is recorded.
        Assert.False(result.Applied);
        Assert.Equal("nexus#42 is in Backlog, not in Review, so there is nothing to decide about it", result.Refusal);
        Assert.Equal("Backlog", result.ResultingLane);
        Assert.Equal(Swimlane.Backlog, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Empty(host.Store.Decisions(workItem.Id));
    }

    [Fact]
    public async Task Request_changes_on_a_parked_work_item_is_refused_with_the_stores_message()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github);
        var workItem = await InReview(host);

        // An approval whose merge did not land parks the work item in Escalated, where a
        // reviewer can still decide — approve or reject only. Sending it round again is
        // refused, and the store says why in its own words.
        using (await host.Board.PostAsJsonAsync(
            $"/api/work-items/{workItem.Id}/decisions",
            new { decision = "approve", feedback = (string?)null }))
        {
        }

        Assert.Equal(Swimlane.Escalated, host.Store.Get(workItem.Id)!.Swimlane);

        using var response = await host.Board.PostAsJsonAsync(
            $"/api/work-items/{workItem.Id}/decisions",
            new { decision = "request-changes", feedback = Feedback });

        var result = await Read(response);
        Assert.False(result.Applied);
        Assert.Equal(
            "nexus#42 is parked in Escalated, and a parked work item is finished by a human: "
                + "merged, or declined. It is not sent round again.",
            result.Refusal);
        Assert.Equal("Escalated", result.ResultingLane);
        Assert.Equal(Swimlane.Escalated, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Single(host.Store.Decisions(workItem.Id));
    }

    /// <summary>A work item with a round behind it, sitting in Review where a reviewer can decide.</summary>
    private static async Task<WorkItem> InReview(FactoryHost host, int issueNumber = 42)
    {
        var workItem = host.Store
            .Intake("nexus", RepoUrl, issueNumber, "A work item, end to end", IssueBody, "main")
            .WorkItem;

        await host.PromoteAsync(workItem.Id);
        await host.Settle();

        Assert.Equal(Swimlane.Review, host.Store.Get(workItem.Id)!.Swimlane);
        return workItem;
    }

    /// <summary>The endpoint's own response, read the way the renderer reads it.</summary>
    private static async Task<DecisionResult> Read(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        return new DecisionResult(
            root.GetProperty("applied").GetBoolean(),
            root.GetProperty("refusal").ValueKind == JsonValueKind.Null
                ? null
                : root.GetProperty("refusal").GetString(),
            root.GetProperty("resultingLane").GetString()!);
    }

    private sealed record DecisionResult(bool Applied, string? Refusal, string ResultingLane);
}
