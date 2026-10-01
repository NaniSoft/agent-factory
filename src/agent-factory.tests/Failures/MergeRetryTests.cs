namespace AgentFactory.Tests.Failures;

using System.Net;
using AgentFactory;
using AgentFactory.Failures;
using AgentFactory.Rounds;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;

/// <summary>
/// A merge that did not land, and the one thing this ticket adds to what parking already
/// decided. #6 parked a failed merge in Escalated precisely so that a merge the repository
/// will not take is not retried unattended every 48 hours for ever, and left the retry
/// policy to here. This is that policy: bounded, classified, paced by a backoff, counted
/// where a restart cannot forget it, and reachable only by a work item that has a merge
/// failure of its own.
/// </summary>
/// <remarks>
/// Every test counts merge attempts. A test that only checked the lane would pass whether
/// the factory tried once or three times, and the difference between those is the whole of
/// the ticket.
/// </remarks>
public class MergeRetryTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";

    private const string IssueBody = "What the issue says, in the maintainer's words.";

    [Fact]
    public async Task A_permanently_failing_merge_is_attempted_once_and_never_again_by_anything()
    {
        // The case #6 arrived at and the ticket's own test asserted. Nothing about it
        // changes except that it is now stated as a policy rather than as a gap: a refusal
        // the factory has read as permanent gets one attempt, and no schedule on earth
        // produces a second.
        var clock = new TestClock();
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().RefusingToMerge(
            FailureClass.Permanent,
            "branch protection would not let it through");
        var agent = new FakeNOpenCode().Producing("src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, clock, agent, github);
        var workItem = await InReview(host);

        using (await Board.DecideAsync(host.Board, workItem.Id, "approve"))
        {
        }

        Assert.Single(github.Merges);
        Assert.Equal(Swimlane.Escalated, host.Store.Get(workItem.Id)!.Swimlane);

        // Nothing on any schedule: the backoff elapsed, the threshold passed, the process
        // restarted. A permanent failure is not a slow one. Bounded settles throughout,
        // because a merge retry without a ceiling never becomes Idle.
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        clock.Advance(FactoryConstants.FeedbackThreshold + TimeSpan.FromDays(4));
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        await using (var restarted = await FactoryHost.StartAsync(root, clock, agent, github))
        {
            await restarted.SettleWithin(TimeSpan.FromSeconds(30));
            Assert.Single(github.Merges);
        }

        // And the record says so, rather than leaving a reviewer to wonder whether the
        // factory is still trying.
        Assert.Equal(1, host.Store.Get(workItem.Id)!.MergeAttempts);
        Assert.Null(host.Store.Get(workItem.Id)!.MergeRetryAfterUtc);
    }

    [Fact]
    public async Task A_transiently_failing_merge_is_attempted_three_times_and_stops_there()
    {
        var clock = new TestClock();
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().RefusingToMerge(
            FailureClass.Transient,
            "the API returned 503");
        var agent = new FakeNOpenCode().Producing("src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, clock, agent, github);
        var workItem = await InReview(host);

        using (await Board.DecideAsync(host.Board, workItem.Id, "approve"))
        {
        }

        Assert.Single(github.Merges);
        Assert.Equal(Swimlane.Escalated, host.Store.Get(workItem.Id)!.Swimlane);

        // Three attempts, each one after the backoff, which is ten seconds and then twenty.
        // A merge that is genuinely transient gets that; a merge that is not gets three and
        // no more, which is the ceiling the design's own constants name.
        clock.Advance(TimeSpan.FromSeconds(10));
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        Assert.Equal(2, github.Merges.Count);

        clock.Advance(TimeSpan.FromSeconds(20));
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        Assert.Equal(3, github.Merges.Count);

        // A fourth, on any schedule, across a restart. This is the assertion that
        // distinguishes a bounded retry from the unbounded one: it is not that the retry
        // stopped, it is that nothing can restart it.
        clock.Advance(TimeSpan.FromDays(4));
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        await using (var restarted = await FactoryHost.StartAsync(root, clock, agent, github))
        {
            await restarted.SettleWithin(TimeSpan.FromSeconds(30));
            Assert.Equal(3, github.Merges.Count);
            Assert.Equal(Swimlane.Escalated, restarted.Store.Get(workItem.Id)!.Swimlane);
        }

        Assert.Equal(3, host.Store.Get(workItem.Id)!.MergeAttempts);
        Assert.Null(host.Store.Get(workItem.Id)!.MergeRetryAfterUtc);
    }

    [Fact]
    public async Task A_merge_that_failed_transiently_is_not_retried_before_its_backoff_has_passed()
    {
        // The backoff is a comparison against the clock, not a wait. A merge is the one
        // place where a retry that is not classified is most dangerous, so the gate is
        // asserted directly: a step asked a second early makes no attempt at all.
        var clock = new TestClock();
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().RefusingToMerge(
            FailureClass.Transient,
            "the API returned 503");
        var agent = new FakeNOpenCode().Producing("src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, clock, agent, github);
        var workItem = await InReview(host);

        using (await Board.DecideAsync(host.Board, workItem.Id, "approve"))
        {
        }

        Assert.Equal(TimeSpan.FromSeconds(10), FactoryConstants.RetryBackoff(1));

        clock.Advance(TimeSpan.FromSeconds(9));
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        Assert.Single(github.Merges);

        clock.Advance(TimeSpan.FromSeconds(1));
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        Assert.Equal(2, github.Merges.Count);
    }

    [Fact]
    public async Task A_retried_merge_that_lands_ships_the_change()
    {
        // The point of widening Escalated: a merge the repository would have taken gets to
        // be taken, without a human having to find the branch and merge it by hand
        // (ADR-0008). A parked work item is parked rather than graveless.
        var clock = new TestClock();
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().RefusingToMerge(
            FailureClass.Transient,
            "the API returned 503");
        var agent = new FakeNOpenCode().Producing("src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, clock, agent, github);
        var workItem = await InReview(host);

        using (await Board.DecideAsync(host.Board, workItem.Id, "approve"))
        {
        }

        Assert.Equal(Swimlane.Escalated, host.Store.Get(workItem.Id)!.Swimlane);

        // The API comes back, and the next step takes the merge it has been offered.
        github.Merging();
        clock.Advance(TimeSpan.FromSeconds(10));
        await host.SettleWithin(TimeSpan.FromSeconds(30));

        Assert.Equal(2, github.Merges.Count);
        Assert.Equal(Swimlane.Done, host.Store.Get(workItem.Id)!.Swimlane);

        // And Done means merged, so the board's Done lane has it and Escalated does not.
        var board = await Board.ReadAsync(host.Board);
        Assert.Contains(workItem.Id.ToString(), board.Swimlane("Done"), StringComparison.Ordinal);
        Assert.DoesNotContain(workItem.Id.ToString(), board.Swimlane("Escalated"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_parked_work_item_is_never_merged_by_the_retry_policy_without_a_merge_failure_of_its_own()
    {
        // The safety property of the whole thing, and the one that makes the widening safe:
        // the retry path keys off a work item's own record of a failed merge, not off its
        // lane. A work item parked by a failed round, or by spent rounds, or by a reviewer
        // declining to look at it, has no such record — so the widening cannot merge it,
        // however long it sits there and however willing the merger is.
        var clock = new TestClock();
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().Merging();
        var agent = new FakeNOpenCode()
            .FailingTransiently()
            .FailingTransiently()
            .FailingTransiently();
        await using var host = await FactoryHost.StartAsync(root, clock, agent, github);
        var workItem = host.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main").WorkItem;
        await host.PromoteAsync(workItem.Id);

        await host.SettleWithin(TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(10));
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(20));
        await host.SettleWithin(TimeSpan.FromSeconds(30));

        Assert.Equal(Swimlane.Escalated, host.Store.Get(workItem.Id)!.Swimlane);

        // The seam would land every merge asked of it, and days pass. Stepped a bounded
        // number of times rather than settled, because a policy that re-opened this work
        // item for a merge is a step that never becomes Idle — and a tight loop like that
        // cannot be bounded by a timeout, so the bound has to be in the number of steps.
        clock.Advance(TimeSpan.FromDays(30));
        for (var step = 0; step < 10; step++)
        {
            await host.Step();
        }

        Assert.Empty(github.Merges);
        Assert.Equal(0, host.Store.Get(workItem.Id)!.MergeAttempts);
    }

    [Fact]
    public async Task A_reviewer_deciding_again_gives_a_work_item_its_own_attempts()
    {
        // A second approval is a second, complete, human-attributable decision rather than
        // a continuation of the first (#20). It gets its own budget, so a reviewer who
        // presses approve again is not joining a queue the factory is already part-way
        // through — and the history still reads as two decisions and one merge.
        var clock = new TestClock();
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().RefusingToMerge(
            FailureClass.Transient,
            "the API returned 503");
        var agent = new FakeNOpenCode()
            .Producing("src/Index.cs +12 -3", "First attempt.")
            .Producing("src/Index.cs +14 -3", "Second attempt.");
        await using var host = await FactoryHost.StartAsync(root, clock, agent, github);
        var workItem = await InReview(host);

        using (await Board.DecideAsync(host.Board, workItem.Id, "approve"))
        {
        }

        // Spent all three of the first approval's attempts on their own.
        clock.Advance(TimeSpan.FromSeconds(10));
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(20));
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        Assert.Equal(3, github.Merges.Count);
        Assert.Equal(3, host.Store.Get(workItem.Id)!.MergeAttempts);

        // The reviewer approves again, and that decision starts a new budget rather than
        // inheriting an exhausted one.
        github.RefusingToMerge(FailureClass.Transient, "the API returned 503");
        using (await Board.DecideAsync(host.Board, workItem.Id, "approve"))
        {
        }

        Assert.Equal(4, github.Merges.Count);
        Assert.Equal(1, host.Store.Get(workItem.Id)!.MergeAttempts);

        var decided = host.Store.Decisions(workItem.Id).ToList();
        Assert.Equal(2, decided.Count);
        Assert.All(decided, decision => Assert.Equal(Decision.Approve, decision.Decision));
    }

    [Fact]
    public async Task The_board_says_how_many_times_a_merge_was_tried_and_whether_more_will_be()
    {
        // Nothing ends in silence, and a merge is the failure a reviewer is least able to
        // infer: they pressed a button, nothing shipped, and the board has to say whether
        // the factory is still trying on its own.
        var clock = new TestClock();
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().RefusingToMerge(
            FailureClass.Transient,
            "the API returned 503");
        var agent = new FakeNOpenCode()
            .Producing("src/Index.cs +12 -3", "First attempt.")
            .Producing("src/Index.cs +14 -3", "Second attempt.");
        await using var host = await FactoryHost.StartAsync(root, clock, agent, github);
        var workItem = await InReview(host);

        using (await Board.DecideAsync(host.Board, workItem.Id, "approve"))
        {
        }

        // One attempt so far, and one more to come.
        var board = await Board.ReadAsync(host.Board);
        Assert.Equal("1", board.Rendered("data-work-item", workItem.Id.ToString(), "data-merge-attempts"));
        Assert.Contains(
            "tried to merge it once",
            board.Rendered("data-work-item", workItem.Id.ToString(), "data-cause"),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "will try again",
            board.Rendered("data-work-item", workItem.Id.ToString(), "data-cause"),
            StringComparison.OrdinalIgnoreCase);

        // All three spent: the count is on the card and the promise of more is gone.
        clock.Advance(TimeSpan.FromSeconds(10));
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(20));
        await host.SettleWithin(TimeSpan.FromSeconds(30));

        board = await Board.ReadAsync(host.Board);
        Assert.Equal("3", board.Rendered("data-work-item", workItem.Id.ToString(), "data-merge-attempts"));

        var cause = board.Rendered("data-work-item", workItem.Id.ToString(), "data-cause");
        Assert.Contains("3 times", cause, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("will try again", cause, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("will not try again on its own", cause, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_merge_the_feedback_threshold_could_not_land_is_retried_only_on_its_own_terms()
    {
        // The timeout path, which is the more dangerous of the two: nobody read this change
        // and the factory is about to put it in the repository anyway. Parking is what took
        // it out of the threshold's reach, and the widening is deliberately not a way back
        // into it — a retried merge is a retried merge, and the threshold is not what
        // prompted it.
        var clock = new TestClock();
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().RefusingToMerge(
            FailureClass.Transient,
            "the API returned 503");
        var agent = new FakeNOpenCode().Producing("src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, clock, agent, github, autoMerge: true);
        var workItem = await InReview(host);

        clock.Advance(FactoryConstants.FeedbackThreshold);
        await host.SettleWithin(TimeSpan.FromSeconds(30));

        Assert.Single(github.Merges);
        Assert.Equal(Swimlane.Escalated, host.Store.Get(workItem.Id)!.Swimlane);

        // Its own bounded retry, on its own backoff.
        clock.Advance(TimeSpan.FromSeconds(10));
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        Assert.Equal(2, github.Merges.Count);
        Assert.Equal(Swimlane.Escalated, host.Store.Get(workItem.Id)!.Swimlane);

        // And it is the retry budget, not the threshold, that decides when it stops. Two
        // more whole feedback thresholds go past — ninety-six hours of a factory that
        // could have re-merged this every 48 hours, which is the loop #6 refused — and the
        // count goes to three and stays there. The threshold is not in Escalated's reach,
        // and the retry is not in its schedule.
        clock.Advance(FactoryConstants.FeedbackThreshold * 2);
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        Assert.Equal(3, github.Merges.Count);
        Assert.Equal(Swimlane.Escalated, host.Store.Get(workItem.Id)!.Swimlane);

        clock.Advance(TimeSpan.FromDays(30));
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        Assert.Equal(3, github.Merges.Count);
    }

    [Fact]
    public async Task A_failed_merge_is_still_refused_on_the_response_the_reviewer_is_holding()
    {
        // Unchanged by any of this, and worth its own assertion: the widening is about the
        // loop's later steps, and it must not make the reviewer's own click look like it
        // worked. Nothing shipped, and the reviewer is told so on the page in front of them.
        var clock = new TestClock();
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().RefusingToMerge(
            FailureClass.Transient,
            "the API returned 503");
        var agent = new FakeNOpenCode().Producing("src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, clock, agent, github);
        var workItem = await InReview(host);

        using var response = await Board.DecideAsync(host.Board, workItem.Id, "approve");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "nexus#42 was approved, but the change was not merged: the API returned 503. "
                + "It is parked in Escalated, where a human can merge it, and nothing shipped.",
            (await Board.ReadAsync(response)).Refusal());

        // And the approval is applied, not pending: it will not be re-applied by a step or
        // by a restart. The retry is the work item's own bounded record, not a decision
        // waiting to be run again.
        var recorded = Assert.Single(host.Store.Decisions(workItem.Id));
        Assert.False(recorded.IsPending);
        Assert.Equal(Swimlane.Escalated, recorded.AppliedTo);
    }

    /// <summary>A work item with one round behind it, sitting in Review where a reviewer can decide.</summary>
    private static async Task<WorkItem> InReview(FactoryHost host, int issueNumber = 42)
    {
        var workItem = host.Store
            .Intake("nexus", RepoUrl, issueNumber, "A work item, end to end", IssueBody, "main")
            .WorkItem;

        await host.PromoteAsync(workItem.Id);

        await host.SettleWithin(TimeSpan.FromSeconds(30));

        Assert.Equal(Swimlane.Review, host.Store.Get(workItem.Id)!.Swimlane);
        return workItem;
    }
}
