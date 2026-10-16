namespace AgentFactory.Infrastructure.WorkItems;

using AgentFactory.Application.WorkItems;
using AgentFactory.Domain.Results;
using AgentFactory.Domain.WorkItems;

/// <summary>
/// The one place a row becomes a record and a record becomes a row.
/// </summary>
/// <remarks>
/// <para>
/// It is a second set of types and the duplication is real, so here is the whole of the
/// justification. The records are the <strong>port's</strong> vocabulary — they are what
/// <c>IWorkItemStore</c>'s signatures name, which is why they cannot live beyond it — and
/// they are immutable positional records with a value object or a computed member in them.
/// A table is a schema: mutable, scalar, one column per property, and free to differ from
/// the shape a caller reads. `RoundResultRecord` in particular cannot be the entity at all,
/// because its diff is five independently-nullable columns rather than a shape mapped as a
/// unit. So the row types are the schema and the records are the contract, and this file is
/// the seam between them — the same seam the hand-written ADO.NET reader and writer were,
/// written once instead of once per column read.
/// </para>
/// <para>
/// **Nothing above the boundary ever sees a row.** The store projects, maps here and
/// returns a record; EF tracks nothing that escapes this assembly.
/// </para>
/// </remarks>
internal static class RowMapping
{
    public static WorkItemRow ToRow(this WorkItem workItem) => new()
    {
        Id = workItem.Id,
        Project = workItem.Project,
        RepoUrl = workItem.RepoUrl,
        IssueNumber = workItem.IssueNumber,
        IssueTitle = workItem.IssueTitle,
        IssueBody = workItem.IssueBody,
        BaseBranch = workItem.BaseBranch,
        Swimlane = workItem.Swimlane,
        RoundCount = workItem.RoundCount,
        CreatedUtc = workItem.CreatedUtc,
        UpdatedUtc = workItem.UpdatedUtc,
        ReviewStartedUtc = workItem.ReviewStartedUtc,
        MergeAttempts = workItem.MergeAttempts,
        MergeRetryAfterUtc = workItem.MergeRetryAfterUtc,
        Kind = workItem.Kind,
        RouteReason = workItem.RouteReason,
        RoutingIssue = workItem.RoutingIssue,
    };

    public static WorkItem ToWorkItem(this WorkItemRow row) => new(
        row.Id,
        row.Project,
        row.RepoUrl,
        row.IssueNumber,
        row.IssueTitle,
        row.IssueBody,
        row.BaseBranch,
        row.Swimlane,
        row.RoundCount,
        row.CreatedUtc,
        row.UpdatedUtc,
        row.ReviewStartedUtc,
        row.MergeAttempts,
        row.MergeRetryAfterUtc,
        row.Kind,
        row.RouteReason,
        row.RoutingIssue);

    public static RoundResultRow ToRow(this RoundResultRecord result) => new()
    {
        WorkItemId = result.WorkItemId,
        RoundNumber = result.RoundNumber,
        Outcome = result.Outcome,
        ResultPayload = result.ResultPayload,
        AgentNote = result.AgentNote,
        StartedUtc = result.StartedUtc,
        CompletedUtc = result.CompletedUtc,
        Attempts = result.Attempts,
        Failure = result.Failure,
        Log = result.Log,
        Diff = result.Diff?.Text,
        DiffBase = result.Diff?.Base,
        DiffTree = result.Diff?.Tree,
        DiffContainerBounded = result.Diff?.ContainerBounded ?? false,
        DiffUnavailable = result.Diff?.UnavailableBecause,
    };

    /// <summary>
    /// One round's diff, assembled out of the five columns it was written into, or null
    /// when the row has none of them — a round whose container never produced a tree, or a
    /// round recorded before the column existed.
    /// </summary>
    /// <remarks>
    /// A row with a diff but no tree is still a diff: the tree is where a reviewer can go
    /// for the parts the board did not render, and its absence does not unmake the text.
    /// Conversely a row whose only diff column is set is the "there was no diff and here is
    /// why" shape, which is read back as that rather than as a round that changed nothing.
    /// </remarks>
    public static RoundResultRecord ToRecord(this RoundResultRow row) => new(
        row.WorkItemId,
        row.RoundNumber,
        row.Outcome,
        row.ResultPayload,
        row.AgentNote,
        row.StartedUtc,
        row.CompletedUtc,
        row.Attempts,
        row.Failure,
        row.Log,
        row.Diff is null && row.DiffUnavailable is null
            ? null
            : new HostDiff(
                row.Diff ?? string.Empty,
                row.DiffBase ?? string.Empty,
                row.DiffTree ?? string.Empty,
                row.DiffContainerBounded,
                row.DiffUnavailable));

    public static DecisionRow ToRow(this DecisionRecord decision) => new()
    {
        WorkItemId = decision.WorkItemId,
        Sequence = decision.Sequence,
        Decision = decision.Decision,
        Feedback = decision.Feedback,
        DecidedUtc = decision.DecidedUtc,
        AppliedTo = decision.AppliedTo,
    };

    public static DecisionRecord ToRecord(this DecisionRow row) => new(
        row.WorkItemId,
        row.Sequence,
        row.Decision,
        row.Feedback,
        row.DecidedUtc,
        row.AppliedTo);

    public static PendingRoundRow ToRow(this PendingRound pending) => new()
    {
        WorkItemId = pending.WorkItemId,
        RoundNumber = pending.RoundNumber,
        Attempt = pending.Attempt,
        Purpose = pending.Purpose,
        AskedUtc = pending.AskedUtc,
        RetryAfterUtc = pending.RetryAfterUtc,
    };

    public static PendingRound ToPendingRound(this PendingRoundRow row) => new(
        row.WorkItemId,
        row.RoundNumber,
        row.Attempt,
        row.Purpose,
        row.AskedUtc,
        row.RetryAfterUtc);
}
