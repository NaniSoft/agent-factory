namespace AgentFactory.Pages;

using AgentFactory.Failures;
using AgentFactory.Rounds;
using AgentFactory.WorkItems;

/// <summary>
/// Why a work item ended up where it is, said in a reviewer's words. Escalated and
/// Rejected are two states with two distinct causes — a failure and a human decline
/// (ADR-0008) — so the board has to say which one it is looking at, and not merely render
/// two lanes whose cards look alike.
/// </summary>
/// <remarks>
/// Derived from the record rather than kept beside it. A separate "cause" column would
/// be a second copy of what the decisions and rounds already say, free to disagree with
/// them, and the work item's own cause is always something a decision or a round did.
/// The merge failure's own message is the one thing here that is not derivable, and it is
/// left in the log and on the response the reviewer was holding rather than written into
/// the work item: what belongs in the record is what happened, and the reason an
/// operation failed is the operation's business (#13).
/// </remarks>
internal static class HowItEnded
{
    public static string Describe(
        WorkItem workItem,
        IReadOnlyList<RoundResultRecord> rounds,
        IReadOnlyList<DecisionRecord> decisions) => workItem.Swimlane switch
    {
        Swimlane.Done => MergedBy(decisions),
        Swimlane.Escalated => ParkedBecause(workItem, rounds, decisions),
        Swimlane.Rejected => DeclinedBy(),
        _ => string.Empty,
    };

    /// <summary>
    /// A merge landed, and who asked for it. A work item in Done with no approval on it
    /// merged because nobody reviewed it, and that is the more dangerous of the two paths
    /// — so it is named as such rather than left to look like a reviewed merge.
    /// </summary>
    private static string MergedBy(IReadOnlyList<DecisionRecord> decisions) =>
        decisions.Any(decision => decision.Decision == Decision.Approve)
            ? "Merged. A reviewer approved it and the change landed."
            : $"Merged. Nobody reviewed it within the {FactoryConstants.FeedbackThresholdText} "
                + "feedback threshold, so the factory merged it. Revert the pull request if that was wrong.";

    /// <summary>
    /// Why a work item is parked. Each of the three ways there is has its own cause, and
    /// saying which is the point: spent rounds, a build that failed, and a merge that did
    /// not land are three different facts about three different things going wrong.
    /// </summary>
    private static string ParkedBecause(
        WorkItem workItem,
        IReadOnlyList<RoundResultRecord> rounds,
        IReadOnlyList<DecisionRecord> decisions)
    {
        var parkedBy = decisions.LastOrDefault(decision => decision.AppliedTo == Swimlane.Escalated);

        if (parkedBy is { Decision: Decision.RequestChanges })
        {
            return $"Escalated. All {FactoryConstants.RoundCeiling} rounds are spent, and the reviewer asked for "
                + "changes, so it is parked for a human rather than built again. It was never merged over that "
                + "objection, and a human can still merge or decline it from here.";
        }

        if (parkedBy is { Decision: Decision.Approve })
        {
            return MergeThatDidNotLand(workItem, "A reviewer approved it, but the merge did not land, so nothing shipped.");
        }

        // The same fact arrived without a reviewer: the feedback threshold fired and the
        // merge the factory asked for on its own did not land. Named separately, because
        // this is the path where nothing was read by anybody and the reviewer needs to know
        // which one they are looking at.
        if (parkedBy is null
            && workItem.MergeAttempts > 0
            && rounds.Any(round => round.Outcome == RoundOutcome.Produced))
        {
            return MergeThatDidNotLand(
                workItem,
                $"Nobody reviewed it within the {FactoryConstants.FeedbackThresholdText} feedback threshold and the "
                    + "merge the factory asked for on its own did not land, so nothing shipped.");
        }

        var failed = rounds.LastOrDefault(round => round.Outcome != RoundOutcome.Produced);
        if (failed is { } round)
        {
            return round.Outcome switch
            {
                // A transient failure the retry policy spent, which is the only one of these
                // a second attempt could have helped: the round is not over because the
                // factory ran out of attempts, not because the change failed.
                RoundOutcome.TimedOut => $"Escalated. Round {round.RoundNumber} came back "
                    + $"{round.Outcome}: it ran past the {FactoryConstants.RoundTimeout.TotalMinutes:0} minute round "
                    + "timeout, so it is a failure and it is parked here rather than tried again. A round that hung "
                    + "once would very likely hang again, and that is the most expensive thing the factory can do. "
                    + "Nothing shipped.",

                _ when round.Failure == FailureClass.Transient => $"Escalated. Round {round.RoundNumber} came back "
                    + $"{round.Outcome} as a transient failure, and it was tried on {round.Attempts} attempts before the "
                    + "factory gave up, so there is no result to review. A round whose own command did not succeed is "
                    + "this shape: a rate limit or a refused provider is the ordinary cause, and the factory spends its "
                    + "attempts on one rather than counting it against the reviewer's rounds. It is parked for a human, "
                    + "who can still merge or decline it from here.",

                _ => $"Escalated. Round {round.RoundNumber} came back {round.Outcome} as a permanent failure, "
                    + "so there is no result to review and the factory did not try again — a build that fails its own "
                    + "tests is an answer, and retrying it would only spend a container to be told the same thing. "
                    + "It is parked for a human, who can still merge or decline it from here.",
            };
        }

        return "Escalated. The factory cannot take this any further, so it is parked for a human, who can still "
            + "merge or decline it from here.";
    }

    private static string DeclinedBy() =>
        "Rejected. A reviewer declined this change and that is final: it will not be built again and it will not "
        + "be merged. A decline protects the repository where a work item left alone does not.";

    /// <summary>
    /// A merge that did not land, and how many times the factory is going to say so. The
    /// count is on the work item rather than in the loop's memory, so a card a reviewer
    /// reads tomorrow still says what has been tried, and whether anything will be.
    /// </summary>
    private static string MergeThatDidNotLand(WorkItem workItem, string whatHappened) =>
        $"Escalated. {whatHappened} The factory has tried to merge it "
            + $"{Times(workItem.MergeAttempts)}"
            + (workItem.MergeRetryAfterUtc is null
                ? " and will not try again on its own: a permanent failure, or the last attempt the retry policy makes. "
                    + "It is parked for a human, who can merge it from here."
                : ", and will try again once that wait has passed. It is parked for a human, who can merge it "
                    + "from here at any time rather than waiting.");

    /// <summary>An attempt count as a reviewer reads it, including the first one.</summary>
    private static string Times(int attempts) =>
        attempts == 1 ? "once" : $"{attempts} times";
}
