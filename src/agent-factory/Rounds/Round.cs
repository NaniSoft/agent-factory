namespace AgentFactory.Rounds;

using AgentFactory.Failures;

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
/// <remarks>
/// <para>
/// <paramref name="Failure"/> is the round's own classification of a failure it did not
/// survive, and it is set by the component that observed the failure — the round runner,
/// which is the only thing that knows whether the round ran at all. It is not set by the
/// loop, and it is not worked out from a message.
/// </para>
/// <para>
/// A <see cref="RoundOutcome.Produced"/> round has no class and cannot have one. That is
/// the structural half of "a build that fails its tests is not retried": a round that ran
/// and whose change failed came back with a result, the failing tests are data inside it,
/// and there is nothing on it for the retry policy to act on (ADR-0001, DESIGN.md).
/// </para>
/// </remarks>
public sealed record RoundResult(
    RoundOutcome Outcome,
    string? ResultPayload,
    string? AgentNote,
    FailureClass? Failure)
{
    /// <summary>
    /// A round that came back with a result, whatever the round's own commands returned. A
    /// build whose tests failed is one of these: the container worked, and the failure is
    /// in the payload for a reviewer to read.
    /// </summary>
    public static RoundResult Produced(string? payload, string? agentNote) =>
        new(RoundOutcome.Produced, payload, agentNote, Failure: null);

    /// <summary>
    /// A round that came back without a result, and what the round runner made of why.
    /// </summary>
    public static RoundResult Failed(FailureClass failure) =>
        new(RoundOutcome.Failed, null, null, failure);

    /// <summary>
    /// A round that ran past the round timeout. Carries no class: the spec's own state
    /// machine gives a timeout a row of its own, straight to Escalated, and the reason is
    /// that a round which hung for ninety minutes is the most expensive failure the
    /// factory has and a second one would very likely be the same ninety minutes.
    /// </summary>
    public static RoundResult TimedOut() =>
        new(RoundOutcome.TimedOut, null, null, Failure: null);

    /// <summary>
    /// The one question the loop's retry policy asks of a round, asked in one place so
    /// that no other code path can have its own answer. True only for a round that came
    /// back without a result <em>and</em> that the round runner said was the attempt
    /// failing rather than the attempt happening.
    /// </summary>
    public bool IsRetryable =>
        Outcome == RoundOutcome.Failed && Failure == FailureClass.Transient;
}
