namespace AgentFactory.WorkItems;

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
    void Move(Guid id, Swimlane swimlane);

    /// <summary>
    /// Keeps one round's result against the work item and counts the round. Results are
    /// appended, never replaced, so a work item that has run three rounds keeps all
    /// three.
    /// </summary>
    RoundResultRecord RecordRound(
        Guid workItemId,
        RoundOutcome outcome,
        string? resultPayload,
        string? agentNote,
        DateTimeOffset startedUtc);

    /// <summary>Every round the work item has run, oldest first.</summary>
    IReadOnlyList<RoundResultRecord> Rounds(Guid workItemId);
}
