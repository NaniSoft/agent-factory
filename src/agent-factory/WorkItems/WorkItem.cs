namespace AgentFactory.WorkItems;

/// <summary>
/// The factory's own record of an issue. It carries the project it belongs to, the
/// issue it came from — number, title and the words the maintainer wrote — the branch
/// the change is built against, and the swimlane it sits in. The board and the
/// orchestrator read this one record, never two.
/// </summary>
/// <remarks>
/// <para>
/// <c>ReviewStartedUtc</c> is when the current review began: the moment the work item
/// entered Review, and null whenever it is not there. The feedback threshold is measured
/// from it, so it is kept as the fact it is rather than read out of a last-modified
/// column that something else could move. A work item that is sent back and reviewed again
/// gets a fresh one, because a work item that left Review is not one that has been left
/// in Review.
/// </para>
/// <para>
/// <c>MergeAttempts</c> and <c>MergeRetryAfterUtc</c> are the factory's own record of
/// trying to ship this work item: how many times it has asked the seam to merge, and when
/// it may ask again. They are the only thing that makes a parked work item eligible for
/// one more attempt, which is the point: a work item parked by a failed build, or by spent
/// rounds, or by a human decline carries none of them, and so nothing the loop does on its
/// own will ever merge it (ADR-0008).
/// </para>
/// <para>
/// <c>Kind</c> is what a routing round decided about this issue (ADR-0013): "nobody has
/// looked at this yet" and "this is ready to build" are different facts and a board that
/// cannot tell them apart is a board whose queue means nothing. The routing pipeline that
/// would set a verdict is not in this branch, so every work item carries
/// <see cref="WorkItemKind.Unrouted"/>, which is the honest default rather than a verdict.
/// </para>
/// </remarks>
public sealed record WorkItem(
    Guid Id,
    string Project,
    string RepoUrl,
    int IssueNumber,
    string IssueTitle,
    string IssueBody,
    string BaseBranch,
    Swimlane Swimlane,
    int RoundCount,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? ReviewStartedUtc,
    int MergeAttempts = 0,
    DateTimeOffset? MergeRetryAfterUtc = null,
    WorkItemKind Kind = WorkItemKind.Unrouted);
