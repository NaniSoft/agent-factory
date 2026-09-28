namespace AgentFactory.Rounds;

using AgentFactory.Failures;

/// <summary>
/// One round: the whole attempt at a work item. The agent builds, tests and corrects
/// itself in one fresh worker container, and this is everything it is given to do it.
/// </summary>
/// <param name="WorkItemId">The work item this round is an attempt at.</param>
/// <param name="Project">The project's name, as its project file spells it.</param>
/// <param name="RepoUrl">The repository the round fetches for itself, over the network.</param>
/// <param name="IssueNumber">Which issue of that repository this is.</param>
/// <param name="IssueTitle">What the issue is called, in the maintainer's words.</param>
/// <param name="IssueBody">
/// What the issue said, in the maintainer's words. Carried because the agent is handed
/// the issue and not the number: a round given a number has to go and fetch what it means,
/// and one given the words does not. It is the same text the board shows, so what the
/// agent is asked to build and what a reviewer reads about it cannot be two different
/// things.
/// </param>
/// <param name="BaseBranch">The commit's starting point: the repository's default branch.</param>
/// <param name="Feedback">
/// The last thing a reviewer said about this work item, kept whole. Empty on a first round
/// and the reviewer's own words on every round after one, because the brief is their words
/// rather than the factory's reading of them.
/// </param>
public sealed record Round(
    Guid WorkItemId,
    string Project,
    string RepoUrl,
    int IssueNumber,
    string IssueTitle,
    string IssueBody,
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
/// <paramref name="Log"/> is the round's log, kept on the result because a round that
/// produced no usable result has nothing else to point a reviewer at, and a result that
/// cannot be read degrades to it rather than to nothing (story 29). It is the end of the
/// log rather than the whole of it, because a ninety-minute build's output does not belong
/// in a database row; the whole log went to the factory's own logging as the round ran
/// it, and this is the part worth having afterwards.
/// </para>
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
/// <param name="Outcome">Whether the round came back with a result at all.</param>
/// <param name="ResultPayload">The derived result, as the board renders it.</param>
/// <param name="AgentNote">The agent's one optional sentence, or null.</param>
/// <param name="Failure">The round runner's own classification, or null for a result.</param>
/// <param name="Log">The end of what the round said, which is all a reviewer has when the payload is not readable.</param>
public sealed record RoundResult(
    RoundOutcome Outcome,
    string? ResultPayload,
    string? AgentNote,
    FailureClass? Failure,
    string? Log)
{
    /// <summary>
    /// A round that came back with a result, whatever the round's own commands returned. A
    /// build whose tests failed is one of these: the container worked, and the failure is
    /// in the payload for a reviewer to read.
    /// </summary>
    public static RoundResult Produced(string? payload, string? agentNote, string? log = null) =>
        new(RoundOutcome.Produced, payload, agentNote, Failure: null, log);

    /// <summary>
    /// A round that came back without a result, and what the round runner made of why.
    /// The log is not optional on this one: a round that produced nothing is invisible
    /// without it, and the classification says there will be no second attempt to produce
    /// something this time.
    /// </summary>
    public static RoundResult Failed(FailureClass failure, string? log = null) =>
        new(RoundOutcome.Failed, null, null, failure, log);

    /// <summary>
    /// A round that ran past the round timeout. Carries no class: the spec's own state
    /// machine gives a timeout a row of its own, straight to Escalated, and the reason is
    /// that a round which hung for ninety minutes is the most expensive failure the
    /// factory has and a second one would very likely be the same ninety minutes.
    /// </summary>
    /// <remarks>
    /// It carries no log either, and the reason is that the loop is the one that decides
    /// a round has timed out: it stopped waiting for a call that had not come back, so at
    /// that moment there is no result and no log to read. The container runtime is still
    /// honouring the round's token and still removing the container, and its own logs
    /// carry the round's output as it happened.
    /// </remarks>
    public static RoundResult TimedOut() =>
        new(RoundOutcome.TimedOut, null, null, Failure: null, null);

    /// <summary>
    /// The one question the loop's retry policy asks of a round, asked in one place so
    /// that no other code path can have its own answer. True only for a round that came
    /// back without a result <em>and</em> that the round runner said was the attempt
    /// failing rather than the attempt happening.
    /// </summary>
    public bool IsRetryable =>
        Outcome == RoundOutcome.Failed && Failure == FailureClass.Transient;
}
