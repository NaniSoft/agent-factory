namespace AgentFactory.UnitTests.Pages;

using AgentFactory.Application.Pages;
using AgentFactory.Application.WorkItems;
using AgentFactory.Domain.Failures;
using AgentFactory.Domain.Rounds;
using AgentFactory.Domain.WorkItems;
using AgentFactory.UnitTests.Boundary;

/// <summary>
/// Why a work item ended where it did, said in a reviewer's words. Escalated and Rejected are two
/// states with two distinct causes, and the board has to say which one a reviewer is looking at
/// rather than render two lanes whose cards look alike.
/// </summary>
public class HowItEndedTests
{
    [Fact]
    public void A_merge_a_reviewer_approved_says_so()
    {
        var ending = HowItEnded.Describe(AWorkItem.For(swimlane: Swimlane.Done), [], [AppliedDecision(Decision.Approve)]);

        Assert.Equal("Merged. A reviewer approved it and the change landed.", ending);
    }

    [Fact]
    public void A_merge_nobody_reviewed_is_named_as_such_rather_than_looking_like_a_reviewed_one()
    {
        // Done with no approval is the more dangerous of the two paths — the feedback threshold
        // shipped it — and it is named as such rather than left to look like a reviewed merge.
        var ending = HowItEnded.Describe(AWorkItem.For(swimlane: Swimlane.Done), [], []);

        Assert.Contains("Nobody reviewed it within the 48 hours feedback threshold", ending, StringComparison.Ordinal);
        Assert.Contains("Revert the pull request", ending, StringComparison.Ordinal);
    }

    [Fact]
    public void A_decline_is_final_and_says_so()
    {
        var ending = HowItEnded.Describe(AWorkItem.For(swimlane: Swimlane.Rejected), [], []);

        Assert.StartsWith("Rejected.", ending, StringComparison.Ordinal);
        Assert.Contains("that is final", ending, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lane_that_is_a_stage_rather_than_an_ending_says_nothing()
    {
        foreach (var lane in new[] { Swimlane.Backlog, Swimlane.Frontier, Swimlane.InProgress, Swimlane.Review, Swimlane.Waiting })
        {
            Assert.Equal(string.Empty, HowItEnded.Describe(AWorkItem.For(swimlane: lane), [], []));
        }
    }

    // ------------------------------------------------------------------ the three ways to park

    [Fact]
    public void A_parked_item_whose_rounds_are_spent_and_whose_reviewer_asked_for_changes_says_that()
    {
        var ending = HowItEnded.Describe(
            AWorkItem.For(swimlane: Swimlane.Escalated),
            [Completed(RoundOutcome.Produced)],
            [AppliedDecision(Decision.RequestChanges)]);

        Assert.Contains("All 3 rounds are spent", ending, StringComparison.Ordinal);
        Assert.Contains("never merged over that objection", ending, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parked_item_a_reviewer_approved_but_whose_merge_failed_says_that()
    {
        var ending = HowItEnded.Describe(
            AWorkItem.For(swimlane: Swimlane.Escalated, mergeAttempts: 1),
            [Completed(RoundOutcome.Produced)],
            [AppliedDecision(Decision.Approve)]);

        Assert.Contains("A reviewer approved it, but the merge did not land", ending, StringComparison.Ordinal);

        // The count is on the work item rather than in the loop's memory, so a card read tomorrow
        // still says what has been tried.
        Assert.Contains("tried to merge it once", ending, StringComparison.Ordinal);
        Assert.Contains("will not try again on its own", ending, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parked_item_nobody_reviewed_and_whose_merge_failed_is_named_separately()
    {
        // The same fact arrived without a reviewer, and the difference matters: this is the path
        // where nothing was read by anybody.
        var ending = HowItEnded.Describe(
            AWorkItem.For(swimlane: Swimlane.Escalated, mergeAttempts: 2),
            [Completed(RoundOutcome.Produced)],
            []);

        Assert.Contains("Nobody reviewed it within the 48 hours feedback threshold", ending, StringComparison.Ordinal);
        Assert.Contains("tried to merge it 2 times", ending, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parked_item_whose_merge_still_has_a_wait_left_says_it_will_try_again()
    {
        var workItem = AWorkItem.For(swimlane: Swimlane.Escalated, mergeAttempts: 1) with
        {
            MergeRetryAfterUtc = AWorkItem.At.AddMinutes(10),
        };

        var ending = HowItEnded.Describe(workItem, [Completed(RoundOutcome.Produced)], []);

        Assert.Contains("will try again once that wait has passed", ending, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parked_item_whose_round_timed_out_says_the_timeout_and_not_the_tests()
    {
        var ending = HowItEnded.Describe(
            AWorkItem.For(swimlane: Swimlane.Escalated),
            [Completed(RoundOutcome.TimedOut, number: 2)],
            []);

        Assert.Contains("Round 2 came back TimedOut", ending, StringComparison.Ordinal);
        Assert.Contains("90 minute round timeout", ending, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parked_item_whose_round_failed_transiently_says_the_factory_spent_its_attempts()
    {
        var ending = HowItEnded.Describe(
            AWorkItem.For(swimlane: Swimlane.Escalated),
            [Completed(RoundOutcome.Failed, FailureClass.Transient)],
            []);

        Assert.Contains("transient failure", ending, StringComparison.Ordinal);
        Assert.Contains("a rate limit or a refused provider", ending, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parked_item_whose_round_failed_permanently_does_not_offer_a_retry()
    {
        var ending = HowItEnded.Describe(
            AWorkItem.For(swimlane: Swimlane.Escalated),
            [Completed(RoundOutcome.Failed, FailureClass.Permanent)],
            []);

        Assert.Contains("permanent failure", ending, StringComparison.Ordinal);
        Assert.Contains("did not try again", ending, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parked_item_with_no_cause_at_all_still_says_something()
    {
        var ending = HowItEnded.Describe(AWorkItem.For(swimlane: Swimlane.Escalated), [], []);

        Assert.Contains("The factory cannot take this any further", ending, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ways_a_work_item_can_end_are_all_distinct()
    {
        // A card that read the same whichever way it ended would be the failure this whole class
        // exists to prevent.
        var endings = new[]
        {
            HowItEnded.Describe(AWorkItem.For(swimlane: Swimlane.Done), [], []),
            HowItEnded.Describe(AWorkItem.For(swimlane: Swimlane.Done), [], [AppliedDecision(Decision.Approve)]),
            HowItEnded.Describe(AWorkItem.For(swimlane: Swimlane.Rejected), [], []),
            HowItEnded.Describe(AWorkItem.For(swimlane: Swimlane.Escalated), [], []),
            HowItEnded.Describe(AWorkItem.For(swimlane: Swimlane.Escalated), [Completed(RoundOutcome.TimedOut)], []),
        };

        Assert.All(endings, ending => Assert.False(string.IsNullOrWhiteSpace(ending)));
        Assert.Equal(endings.Length, endings.Distinct(StringComparer.Ordinal).Count());
    }

    private static DecisionRecord AppliedDecision(Decision decision) =>
        new(Guid.NewGuid(), 1, decision, "the reviewer's own words", AWorkItem.At, Swimlane.Escalated);

    private static RoundResultRecord Completed(RoundOutcome outcome, FailureClass? failure = null, int number = 1) =>
        new(
            Guid.NewGuid(),
            number,
            outcome,
            "the round's own record",
            null,
            AWorkItem.At,
            AWorkItem.At,
            Failure: failure);
}
