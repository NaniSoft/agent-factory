namespace AgentFactory.WorkItems;

/// <summary>
/// The factory's own record of an issue. It carries the project it belongs to, the
/// issue it came from — number, title and the words the maintainer wrote — the branch
/// the change is built against, and the swimlane it sits in. The board and the
/// orchestrator read this one record, never two.
/// </summary>
/// <remarks>
/// <c>ReviewStartedUtc</c> is when the current review began: the moment the work item
/// entered Review, and null whenever it is not there. The feedback threshold is measured
/// from it, so it is kept as the fact it is rather than read out of a last-modified
/// column that something else could move. A work item that is sent back and reviewed again
/// gets a fresh one, because a work item that left Review is not one that has been left
/// in Review.
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
    DateTimeOffset? ReviewStartedUtc);
