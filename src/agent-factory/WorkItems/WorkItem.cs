namespace AgentFactory.WorkItems;

/// <summary>
/// The factory's own record of an issue. It carries the project it belongs to, the
/// issue it came from — number, title and the words the maintainer wrote — the branch
/// the change is built against, and the swimlane it sits in. The board and the
/// orchestrator read this one record, never two.
/// </summary>
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
    DateTimeOffset UpdatedUtc);
