namespace AgentFactory.Tests.Endings;

using System.Net;
using AgentFactory;
using AgentFactory.Rounds;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;

/// <summary>
/// The round ceiling, and the parking that follows it. Three rounds per work item, and a
/// third request for changes escalates rather than starting a fourth round — because the
/// ceiling is a brake on the agent and the reviewer disagreeing for ever, and a ceiling
/// that spends one more round before it bites is not a ceiling (ADR-0001, ADR-0005).
/// Driven over the board's own HTTP surface, the way a reviewer sends a change back, with
/// only the agent, the clock and GitHub faked. Nothing here waits: the ceiling and the
/// threshold are both comparisons against the fake clock.
/// </summary>
/// <remarks>
/// Most of these run against a merger that would land every merge it is asked for, and
/// that is deliberate. "Exhaustion never merges" is only a claim about the factory if the
/// factory could have merged; a seam that refused everything would let a loop that merged
/// on exhaustion pass, because the merge would have failed for the wrong reason.
/// </remarks>
public class ExhaustionTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";

    private const string IssueBody = "What the issue says, in the maintainer's words.";

    private const string OutsideTheLock = "The null check is outside the lock; move it inside.";

    private const string StillOutside = "Still outside the lock. That is the whole of it.";

    private const string ThirdTime = "Third time asking. It is outside the lock.";

    [Fact]
    public async Task A_third_request_for_changes_escalates_and_never_starts_a_fourth_round()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().Merging();
        var agent = ThreeRounds();
        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github);
        var workItem = await InReview(host);

        // Two rounds of disagreement, one reviewer's words at a time. Each of the first
        // two requests costs a round and puts the change back into the build.
        await SendBack(host, workItem, OutsideTheLock);
        await SendBack(host, workItem, StillOutside);

        Assert.Equal(Swimlane.Review, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Equal(3, agent.AskedFor.Count);
        Assert.Equal(3, host.Store.Get(workItem.Id)!.RoundCount);

        // The third. There is no fourth round to hand the change to, so it escalates
        // rather than starting one.
        await SendBack(host, workItem, ThirdTime);

        // Three rounds asked for, three rounds run. A fourth was never asked for at all,
        // which is the whole of the ceiling: a work item cannot buy its way past it by
        // disagreeing once more.
        Assert.Equal(3, agent.AskedFor.Count);
        Assert.Equal(3, host.Store.Get(workItem.Id)!.RoundCount);
        Assert.Equal(Swimlane.Escalated, host.Store.Get(workItem.Id)!.Swimlane);

        // And nothing shipped. A change the reviewer has called wrong three times is never
        // merged over that objection, so the seam behind a working merger is not even
        // asked: exhaustion is not a decision to ship.
        Assert.Empty(github.Merges);

        // The rounds it ran and the rounds it is charged for are the same rounds, so the
        // board's "round 3 of 3" is honest rather than a round ahead or a round behind.
        Assert.Equal(
            host.Store.Get(workItem.Id)!.RoundCount,
            host.Store.Rounds(workItem.Id).Count);

        // Asked again, repeatedly, and with time passing. The machine has no transition
        // out of a parked work item, so a fourth round is not merely refused: it is not
        // reachable.
        await host.Settle();
        host.Clock.Advance(TimeSpan.FromHours(1));
        await host.Settle();

        Assert.Equal(3, agent.AskedFor.Count);
        Assert.Equal(3, host.Store.Get(workItem.Id)!.RoundCount);
        Assert.Equal(Swimlane.Escalated, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Empty(github.Merges);
    }

    [Fact]
    public async Task A_work_item_out_of_rounds_is_never_merged_however_long_it_is_left_alone()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().Merging();
        var agent = ThreeRounds();
        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github, autoMerge: true);
        var workItem = await Exhausted(host);

        // Forty-eight hours is where the factory merges a work item nobody reviewed. An
        // exhausted one is not waiting in Review, and the difference between the two is
        // the rule ADR-0008 exists for: ignoring a work item merges it, and objecting to
        // one parks it.
        host.Clock.Advance(FactoryConstants.FeedbackThreshold + TimeSpan.FromHours(1));
        await host.Settle();

        Assert.Equal(Swimlane.Escalated, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Empty(github.Merges);

        // And not for a second threshold either. A parked work item is not one the loop
        // keeps revisiting, so nothing about the passage of time becomes a merge attempt.
        // Were the merge check to reach any lane but Review, this is where it would show.
        host.Clock.Advance(TimeSpan.FromDays(4));
        await host.Settle();

        Assert.Empty(github.Merges);
        Assert.Equal(Swimlane.Escalated, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Equal(3, agent.AskedFor.Count);
    }

    [Fact]
    public async Task A_parked_work_item_can_still_be_merged_from_the_board()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().Merging();
        await using var host = await FactoryHost.StartAsync(root, agent: ThreeRounds(), github: github);
        var workItem = await Exhausted(host);

        // Parking is not a grave. The board renders a decision on it, and a human
        // finishing the work item does not mean leaving the tool to find a branch and
        // merge it by hand (ADR-0008).
        var parked = await Board.ReadAsync(host.Board);
        Assert.Contains(workItem.Id.ToString(), parked.Swimlane("Escalated"), StringComparison.Ordinal);
        Assert.NotEqual(string.Empty, parked.DecisionFormFor(workItem.Id));

        using var response = await Board.DecideAsync(host.Board, workItem.Id, "approve");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([new MergeAttempt(RepoUrl, 42)], github.Merges);
        Assert.Equal(Swimlane.Done, host.Store.Get(workItem.Id)!.Swimlane);

        var board = await Board.ReadAsync(host.Board);
        Assert.Contains(workItem.Id.ToString(), board.Swimlane("Done"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_parked_work_item_can_still_be_rejected_from_the_board()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().Merging();
        await using var host = await FactoryHost.StartAsync(root, agent: ThreeRounds(), github: github);
        var workItem = await Exhausted(host);

        using var response = await Board.DecideAsync(host.Board, workItem.Id, "reject");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Swimlane.Rejected, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Empty(github.Merges);

        // Rejection reached from a parked work item is the same final thing it is
        // anywhere else, and the decision that put it there says which lane it put it in.
        var recorded = Assert.Single(
            host.Store.Decisions(workItem.Id),
            decision => decision.AppliedTo == Swimlane.Rejected);
        Assert.Equal(Decision.Reject, recorded.Decision);

        // And a declined work item is finished: nothing to press on it any more.
        var board = await Board.ReadAsync(host.Board);
        Assert.Contains(workItem.Id.ToString(), board.Swimlane("Rejected"), StringComparison.Ordinal);
        Assert.Equal(string.Empty, board.DecisionFormFor(workItem.Id));
    }

    [Fact]
    public async Task A_work_item_a_reviewer_declined_cannot_be_reopened_by_any_decision()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().Merging();
        var agent = SixRounds();
        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github, autoMerge: true);
        var workItem = await Exhausted(host);

        // Declined from Escalated, so the lane it was reached from is the new one: parking
        // hands the work item to a human, and a human is allowed to decline it.
        using (await Board.DecideAsync(host.Board, workItem.Id, "reject"))
        {
        }

        Assert.Equal(Swimlane.Rejected, host.Store.Get(workItem.Id)!.Swimlane);

        // A work item in Review alongside it, so the board is still rendering a form and a
        // post made by hand is refused for what it is asking rather than for having no
        // token to post with.
        var waiting = await InReview(host, 43);
        Assert.Equal(Swimlane.Review, host.Store.Get(waiting.Id)!.Swimlane);

        // The rounds this work item ran, so the ceiling assertion below is about it and
        // not about the work item alongside it.
        var roundsRun = host.Store.Rounds(workItem.Id).Count;

        foreach (var decision in new[] { "approve", "request-changes", "reject" })
        {
            using var response = await Board.PostByHandAsync(
                host.Board,
                workItem.Id,
                decision,
                decision == "request-changes" ? "One more go, please." : null);

            var board = await Board.ReadAsync(response);
            Assert.Equal(
                "nexus#42 is in Rejected, not in Review, so there is nothing to decide about it",
                board.Refusal());

            Assert.Equal(Swimlane.Rejected, host.Store.Get(workItem.Id)!.Swimlane);
            Assert.Equal(FactoryConstants.RoundCeiling, host.Store.Get(workItem.Id)!.RoundCount);
            Assert.Equal(roundsRun, host.Store.Rounds(workItem.Id).Count);
        }

        // No merge on its behalf, ever: not from a post made by hand, and not from a seam
        // that would land every merge asked of it. A decline protects the repository,
        // which is the other half of the asymmetry — ignoring merges, and saying no does
        // not.
        Assert.DoesNotContain(github.Merges, merge => merge.IssueNumber == 42);
        Assert.Equal(
            [Decision.RequestChanges, Decision.RequestChanges, Decision.RequestChanges, Decision.Reject],
            host.Store.Decisions(workItem.Id).Select(decision => decision.Decision));

        // And time passing does not undo it. The work item alongside it is in Review and
        // nobody has looked at it, so the threshold merges *that* one and leaves this one
        // exactly where a reviewer put it — which is the asymmetry, arrived at from the
        // other side.
        host.Clock.Advance(FactoryConstants.FeedbackThreshold + TimeSpan.FromDays(1));
        await host.Settle();

        Assert.Equal(Swimlane.Rejected, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.DoesNotContain(github.Merges, merge => merge.IssueNumber == 42);
        Assert.Equal(Swimlane.Done, host.Store.Get(waiting.Id)!.Swimlane);
    }

    [Fact]
    public async Task A_parked_work_item_is_not_sent_round_again_on_a_reviewers_request()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().Merging();
        var agent = new FakeNOpenCode().Throwing("the round came back without a result");
        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github);

        // A work item parked by a round that never came back, so it has rounds to spare
        // and no reviewer has objected to anything at all. It is still parked.
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main")
            .WorkItem;
        await host.PromoteAsync(workItem.Id);

        await host.Settle();
        Assert.Equal(Swimlane.Escalated, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Equal(1, host.Store.Get(workItem.Id)!.RoundCount);
        Assert.Single(agent.AskedFor);

        // Requesting changes on it is refused, and the refusal is about parking rather
        // than about the rounds. What finishes a parked work item is a human merging it
        // or declining it; re-opening the build from here is the retry policy's business
        // (#7), and a button that always failed would be a lie the board told.
        using var response = await Board.PostByHandAsync(
            host.Board,
            workItem.Id,
            "request-changes",
            "Have another go, then.");

        var board = await Board.ReadAsync(response);
        Assert.Equal(
            "nexus#42 is parked in Escalated, and a parked work item is finished by a human: "
                + "merged, or declined. It is not sent round again.",
            board.Refusal());

        Assert.Equal(Swimlane.Escalated, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Empty(host.Store.Decisions(workItem.Id));
        Assert.Single(agent.AskedFor);
    }

    [Fact]
    public async Task A_merge_the_seam_could_not_land_parks_the_work_item_where_a_human_can_finish_it()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().RefusingToMerge("branch protection would not let it through");
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github, autoMerge: true);
        var workItem = await InReview(host);

        // The reviewer approves and the merge does not land. Where that leaves the work
        // item is this ticket's decision, and the answer is Escalated: a factory that
        // cannot ship what a human approved has failed, and a failure is what Escalated
        // is for. Leaving it in Review would leave it inside the feedback timeout's
        // reach, and a merge known not to work would then be retried unattended every 48
        // hours for ever — which is the failure the timeout path exists not to be.
        using var response = await Board.DecideAsync(host.Board, workItem.Id, "approve");

        Assert.Equal(Swimlane.Escalated, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.DoesNotContain(host.Store.List(), item => item.Swimlane == Swimlane.Done);

        // The approval is neither lost nor erased: it is on the record, marked as the
        // thing that moved the work item, saying which lane it moved it to.
        var recorded = Assert.Single(host.Store.Decisions(workItem.Id));
        Assert.Equal(Decision.Approve, recorded.Decision);
        Assert.Equal(Swimlane.Escalated, recorded.AppliedTo);
        Assert.False(recorded.IsPending, "the loop has acted on it; its answer was not Done");

        // And it is not silent, in both of the places a reviewer would look: the response
        // they are holding, and the card afterwards.
        var held = await Board.ReadAsync(response);
        Assert.Equal(
            "nexus#42 was approved, but the change was not merged: branch protection would not let it through. "
                + "It is parked in Escalated, where a human can merge it, and nothing shipped.",
            held.Refusal());

        var board = await Board.ReadAsync(host.Board);
        var rendered = Assert.Single(board.DecisionsOn(workItem.Id));
        Assert.Equal("approve", rendered.Decision);
        Assert.Equal("Escalated", rendered.AppliedTo);
        Assert.Contains(
            "merge",
            board.Rendered("data-work-item", workItem.Id.ToString(), "data-cause"),
            StringComparison.OrdinalIgnoreCase);

        // Finishable: a parked work item is one a human can still finish, and the board
        // still offers the button.
        Assert.NotEqual(string.Empty, board.DecisionFormFor(workItem.Id));

        // Not retried. Whether a merge that failed is worth another attempt, how many, and
        // how soon is the retry ticket's (#7). Until then the single attempt a reviewer
        // can see and make again themselves is the whole of it.
        await host.Settle();
        Assert.Single(github.Merges);

        // Not across a restart, and not once the feedback threshold has passed: a parked
        // work item is outside the timeout's reach in both.
        host.Clock.Advance(FactoryConstants.FeedbackThreshold + TimeSpan.FromDays(1));
        await using (var restarted = await FactoryHost.StartAsync(root, agent: agent, github: github, autoMerge: true))
        {
            await restarted.Settle();
            Assert.Single(github.Merges);
            Assert.Equal(Swimlane.Escalated, restarted.Store.Get(workItem.Id)!.Swimlane);
        }
    }

    /// <summary>A work item with one round behind it, sitting in Review where a reviewer can decide.</summary>
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

    /// <summary>Three rounds the reviewer keeps sending back, and the work item left parked.</summary>
    private static async Task<WorkItem> Exhausted(FactoryHost host)
    {
        var workItem = await InReview(host);

        await SendBack(host, workItem, OutsideTheLock);
        await SendBack(host, workItem, StillOutside);
        await SendBack(host, workItem, ThirdTime);

        Assert.Equal(Swimlane.Escalated, host.Store.Get(workItem.Id)!.Swimlane);
        return workItem;
    }

    /// <summary>One reviewer's request for changes, pressed on the board, and the round it starts.</summary>
    private static async Task SendBack(FactoryHost host, WorkItem workItem, string words)
    {
        using var response = await Board.DecideAsync(host.Board, workItem.Id, "request-changes", words);

        Assert.Null((await Board.ReadAsync(response)).Refusal());
        await host.Settle();
    }

    /// <summary>Three rounds waiting to be asked for, for a test that never gets that far.</summary>
    private static FakeNOpenCode ThreeRounds() => new FakeNOpenCode()
        .Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.")
        .Yielding(RoundOutcome.Produced, "src/Index.cs +14 -3", "Second attempt.")
        .Yielding(RoundOutcome.Produced, "src/Index.cs +15 -3", "Third attempt.");

    /// <summary>
    /// Three more on top, for a test that exhausts one work item and then needs a second
    /// one in Review alongside it. The rounds are per work item, so the second work item
    /// gets its own budget rather than whatever the first left over.
    /// </summary>
    private static FakeNOpenCode SixRounds() => new FakeNOpenCode()
        .Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.")
        .Yielding(RoundOutcome.Produced, "src/Index.cs +14 -3", "Second attempt.")
        .Yielding(RoundOutcome.Produced, "src/Index.cs +15 -3", "Third attempt.")
        .Yielding(RoundOutcome.Produced, "src/Review.cs +4 -0", "First attempt.")
        .Yielding(RoundOutcome.Produced, "src/Review.cs +6 -0", "Second attempt.")
        .Yielding(RoundOutcome.Produced, "src/Review.cs +8 -0", "Third attempt.");
}
