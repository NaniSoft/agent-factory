namespace AgentFactory.WorkItems;

using AgentFactory.Failures;
using AgentFactory.Rounds;

/// <summary>
/// The store of record. Work items are persisted, so a restart does not discard an
/// agent's work. One writer, in one process, against one SQLite file (ADR-0009).
/// </summary>
public interface IWorkItemStore
{
    /// <summary>
    /// Makes the issue a work item, or says the work item is already there. Intake is
    /// the only thing that calls this, and it is idempotent: the same issue polled twice
    /// is one work item, and the second poll leaves the first work item exactly as it
    /// was — same swimlane, same base, same rounds.
    /// </summary>
    /// <remarks>
    /// A work item is born in Backlog and nowhere else. Every other lane is reached by
    /// the loop moving one that is already there, so "a work item started outside the
    /// loop" is not a state this store can represent.
    /// </remarks>
    WorkItemRecord Intake(
        string project,
        string repoUrl,
        int issueNumber,
        string issueTitle,
        string issueBody,
        string baseBranch);

    WorkItem? Get(Guid id);

    /// <summary>Every work item, oldest first.</summary>
    IReadOnlyList<WorkItem> List();

    /// <summary>
    /// Moves a work item to another swimlane. This is the only way a work item changes
    /// lane, so the lane a reviewer sees is the lane the loop decided on.
    /// </summary>
    /// <remarks>
    /// A move into Review starts the work item's review, and a move out of it ends that
    /// one, so the moment a reviewer has been waiting is kept with the move rather than
    /// inferred from the record's last-modified time. It is what the feedback threshold
    /// is measured from.
    /// </remarks>
    void Move(Guid id, Swimlane swimlane);

    /// <summary>
    /// Keeps one round's result against the work item and counts the round. Results are
    /// appended, never replaced, so a work item that has run three rounds keeps all
    /// three.
    /// </summary>
    /// <param name="attempts">
    /// How many times the factory asked for this round. Recorded rather than inferred,
    /// because a round is charged to the ceiling once however many attempts it took, and
    /// a reader of the board needs to be able to see the difference.
    /// </param>
    /// <param name="failure">
    /// The round runner's classification of a failure the round did not survive, and null
    /// for a round that came back with a result — including one whose own tests failed.
    /// </param>
    RoundResultRecord RecordRound(
        Guid workItemId,
        RoundOutcome outcome,
        string? resultPayload,
        string? agentNote,
        DateTimeOffset startedUtc,
        int attempts = 1,
        FailureClass? failure = null);

    /// <summary>Every round the work item has run, oldest first.</summary>
    IReadOnlyList<RoundResultRecord> Rounds(Guid workItemId);

    /// <summary>
    /// Records that the factory tried to ship this work item and it did not land, and
    /// when it may try again. The count goes up and the gate is set in one write, so a
    /// second attempt cannot be made without the first being on the record.
    /// </summary>
    /// <remarks>
    /// This is what makes one more attempt possible at all, and keeping it here rather than
    /// in the loop's memory is what stops a restart granting a fresh budget to a merge the
    /// repository has already refused. A work item with no merge attempt against it can
    /// never be merged by anything the loop does on its own.
    /// </remarks>
    /// <param name="retryAfterUtc">
    /// When another attempt may be made, or null when there will not be one: a permanent
    /// failure is escalated without a retry, so there is no time after which to look again.
    /// </param>
    void RecordMergeFailure(Guid workItemId, FailureClass failure, DateTimeOffset? retryAfterUtc);

    /// <summary>
    /// Records a reviewer's decision about a work item: which of the three it was, and
    /// the words they wrote. The only way a decision is made, and the only place the
    /// reviewer's own reasons are kept.
    /// </summary>
    /// <remarks>
    /// Recording a decision does not move the work item. Which swimlane a decision means
    /// is the loop's policy, so the board records and the loop applies — and a decision
    /// that turned out not to be applicable is still a decision that was made, and is
    /// still what the reviewer said.
    ///
    /// It does clear the work item's merge attempts, because a decision is a human saying
    /// something about the change again: approving a work item whose merge failed is a
    /// second, complete, attributable decision and it is entitled to its own attempts
    /// rather than inheriting the ones a previous approval spent.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">There is no such work item.</exception>
    /// <exception cref="InvalidOperationException">
    /// The work item is in no lane a reviewer can act on — nothing outside
    /// <see cref="Swimlanes.Decidable"/>, which is what keeps Rejected final without a
    /// later caller having to remember; or the work item is parked and the decision
    /// requests changes, because a parked work item is finished by a human rather than
    /// sent round again (ADR-0008); or the decision requests changes and carries no
    /// reasons, which would leave the next round with no brief.
    /// </exception>
    DecisionRecord RecordDecision(Guid workItemId, Decision decision, string? feedback);

    /// <summary>Every decision the work item has been given, oldest first.</summary>
    IReadOnlyList<DecisionRecord> Decisions(Guid workItemId);

    /// <summary>
    /// The loop's answer to a decision: the work item moves, and the decision is marked
    /// as the thing that moved it. Both happen or neither does, in one transaction,
    /// because a work item that had moved with its decision still pending would be a
    /// decision the loop applied a second time — and a second round of building that
    /// nobody asked for.
    /// </summary>
    /// <param name="swimlane">Where the decision put the work item. The loop decides it;
    /// the store only records it.</param>
    /// <exception cref="KeyNotFoundException">
    /// There is no such decision of that work item's, or the loop has already applied it.
    /// </exception>
    void ApplyDecision(Guid workItemId, int sequence, Swimlane swimlane);
}
