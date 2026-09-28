namespace AgentFactory.Rounds;

/// <summary>
/// One round: the whole attempt at a work item. The agent builds, tests and corrects
/// itself in one fresh worker container, and this is everything it is given to do it.
/// </summary>
public sealed record Round(
    Guid WorkItemId,
    string Project,
    string RepoUrl,
    int IssueNumber,
    string BaseBranch,
    string Feedback);

/// <summary>How a round ended. What the factory does about it is policy, not observation.</summary>
public enum RoundOutcome
{
    /// <summary>The round came back with a result.</summary>
    Produced,

    /// <summary>The round came back without one.</summary>
    Failed,

    /// <summary>The round went past the round timeout.</summary>
    TimedOut,
}

/// <summary>
/// What one round produced. The payload is the record the factory derives by observing
/// the container — files changed, commands run, test outcomes — and the note is the
/// agent's optional prose, which a round loses a sentence rather than a record without.
/// </summary>
public sealed record RoundResult(
    RoundOutcome Outcome,
    string? ResultPayload,
    string? AgentNote);
