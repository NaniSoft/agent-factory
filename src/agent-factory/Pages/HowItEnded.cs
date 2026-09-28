namespace AgentFactory.Pages;

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
            return "Escalated. A reviewer approved it, but the merge did not land, so nothing shipped. It is "
                + "parked for a human, who can merge it from here; the factory will not keep trying on its own.";
        }

        var failed = rounds.LastOrDefault(round => round.Outcome != RoundOutcome.Produced);
        if (failed is { } round)
        {
            return $"Escalated. Round {round.RoundNumber} came back {round.Outcome}, so there is no result to "
                + "review. It is parked for a human, who can still merge or decline it from here.";
        }

        return "Escalated. The factory cannot take this any further, so it is parked for a human, who can still "
            + "merge or decline it from here.";
    }

    private static string DeclinedBy() =>
        "Rejected. A reviewer declined this change and that is final: it will not be built again and it will not "
        + "be merged. A decline protects the repository where a work item left alone does not.";
}
