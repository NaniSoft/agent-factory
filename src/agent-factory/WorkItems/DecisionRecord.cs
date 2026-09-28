namespace AgentFactory.WorkItems;

/// <summary>
/// One decision, as the reviewer made it and the factory kept it. The words are theirs:
/// the store records what was said and does not paraphrase, complete or replace it, so
/// a round that is handed this brief was handed what the reviewer actually wrote.
/// </summary>
/// <param name="WorkItemId">Which work item was decided about.</param>
/// <param name="Sequence">Where it sits in the order the work item was decided in.</param>
/// <param name="Decision">Which of the three it was.</param>
/// <param name="Feedback">What the reviewer wrote, kept whole.</param>
/// <param name="DecidedUtc">When the reviewer decided, by the clock the factory is wired to.</param>
/// <param name="AppliedTo">
/// The swimlane the loop put the work item in when it applied this decision, and null
/// until it did. A decision is a thing a reviewer did whether or not the loop has got
/// to it yet, so "not applied yet" is a state this record can be in rather than a
/// decision that has not happened.
/// </param>
public sealed record DecisionRecord(
    Guid WorkItemId,
    int Sequence,
    Decision Decision,
    string Feedback,
    DateTimeOffset DecidedUtc,
    Swimlane? AppliedTo)
{
    /// <summary>A decision the loop has not acted on yet: the next transition that can apply.</summary>
    public bool IsPending => AppliedTo is null;
}
