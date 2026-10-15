namespace AgentFactory.Rounds;

using AgentFactory.Domain.Failures;
using AgentFactory.Domain.Results;
using AgentFactory.Domain.Rounds;

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
/// <param name="RoundNumber">
/// Which round of this work item this is, counting from one. Carried rather than inferred
/// where the round's files land, because a work item's rounds are three separate attempts
/// with three separate trees and a landing path without the number puts the second one
/// inside the first (#22).
/// </param>
/// <param name="Attempt">
/// Which attempt at this round this is, counting from one. A round that failed transiently
/// is asked for again as the <em>same</em> round, and each attempt is its own container with
/// its own tree — so the two are separate facts, and the attempt is what makes a retried
/// round's landing directory its own rather than the one the first attempt used.
/// </param>
/// <param name="Purpose">
/// Whether this round fetches the repository and builds something, or reads the issue and
/// decides what kind of work it is (ADR-0015). It is carried rather than worked out from the
/// work item's kind because the round runner is the component that has to act on it — by
/// choosing a script and by deciding whether to put a repository URL in the container's
/// environment at all — and neither of those is the store's business.
/// </param>
/// <param name="Conversation">
/// The issue's comments, in order — the conversation the issue's body started. An issue is
/// a conversation and not a document: a Grill's questions are answered in the comments of
/// the issue the factory filed, and a round handed the body alone would be asked to work
/// from the question while the answers sat next to it unread. Empty when the issue has no
/// comments, and empty when the conversation could not be read — which the loop says out
/// loud in its own log, because a round that answered a question without the answers would
/// be a round that looked like it had.
/// </param>
/// <remarks>
/// A round carries its own number rather than being asked for one, for the same reason it
/// carries the work item's id: the round runner has to name a directory the round's files
/// land in, and a path built from the work item alone is one directory for every round of
/// that work item. That is not a tidiness concern — <c>docker cp</c> copies a directory
/// <em>into</em> a destination that already exists rather than replacing it, so a second
/// round's tree landed inside the first's, and the board and the merger then read the
/// first (#22).
/// </remarks>
public sealed record Round(
    Guid WorkItemId,
    string Project,
    string RepoUrl,
    int IssueNumber,
    string IssueTitle,
    string IssueBody,
    string BaseBranch,
    string Feedback,
    int RoundNumber,
    int Attempt,
    RoundPurpose Purpose = RoundPurpose.Build,
    IReadOnlyList<GitHub.IssueComment>? Conversation = null)
{
    /// <summary>
    /// The issue's comments, in order, or an empty list when the issue has none or the
    /// conversation could not be read — which the loop logs rather than leaving to a round
    /// that cannot tell the difference.
    /// </summary>
    public IReadOnlyList<GitHub.IssueComment> Conversation { get; init; } = Conversation ?? [];
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
/// to completion and whose change failed came back with a result, the failing tests are
/// data inside it, and there is nothing on it for the retry policy to act on (ADR-0001,
/// DESIGN.md). It is also what keeps a rate-limited round and a test-failed round apart:
/// they differ in <em>which</em> number was non-zero, and the two numbers go to two
/// different places, so neither is ever a flag the other also sets.
/// </para>
/// </remarks>
/// <param name="Outcome">Whether the round's own command succeeded.</param>
/// <param name="ResultPayload">The derived result, as the board renders it.</param>
/// <param name="AgentNote">The agent's one optional sentence, or null.</param>
/// <param name="Failure">The round runner's own classification, or null for a result.</param>
/// <param name="Log">The end of what the round said, which is all a reviewer has when the payload is not readable.</param>
/// <param name="Diff">
/// The round's change, generated on the host from the tree the round left, and separately
/// from the payload because it is a different observation: the payload is what the
/// container recorded about itself, and this is <c>git diff</c> run against the lifted
/// tree after the container was gone. It is carried even when the payload is missing or
/// unreadable — that is the round a reviewer most needs to see something of — and it is
/// null only when there was no tree to look at at all.
/// </param>
public sealed record RoundResult(
    RoundOutcome Outcome,
    string? ResultPayload,
    string? AgentNote,
    FailureClass? Failure,
    string? Log,
    HostDiff? Diff = null,
    Routing.RoutingOutcome? Routing = null)
{
    /// <summary>
    /// A round whose own last command returned zero, whatever the commands inside it
    /// returned. A build whose tests failed is one of these: the round finished, and the
    /// failure is in the payload for a reviewer to read.
    /// </summary>
    public static RoundResult Produced(
        string? payload,
        string? agentNote,
        string? log = null,
        HostDiff? diff = null,
        Routing.RoutingOutcome? routing = null) =>
        new(RoundOutcome.Produced, payload, agentNote, Failure: null, log, diff, routing);

    /// <summary>
    /// A round that did not run to completion, and what the round runner made of why.
    /// The log is not optional on this one: a round that produced nothing is invisible
    /// without it, and the classification says there will be no second attempt to produce
    /// something this time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A diff is carried on this shape too, and that is the point of it being a field of
    /// its own. A round that ran and then broke left a tree on the host whatever else is
    /// missing, and what it managed to change before it broke is the one thing a reviewer
    /// most wants to know about it.
    /// </para>
    /// <para>
    /// The payload is carried for the same reason, and it is what stops a rate-limited
    /// round from being a card with nothing on it: the exit code and the provider's own
    /// error are in there, and they are the only account of what stopped the round.
    /// </para>
    /// </remarks>
    public static RoundResult Failed(
        FailureClass failure,
        string? log = null,
        HostDiff? diff = null,
        string? payload = null,
        Routing.RoutingOutcome? routing = null) =>
        new(RoundOutcome.Failed, payload, null, failure, log, diff, routing);

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
    /// that no other code path can have its own answer. True only for a round that did not
    /// run to completion <em>and</em> that the round runner said was the attempt failing
    /// rather than the attempt happening.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A rate-limited round is one of these and a test-failed round is not, and they do not
    /// share a flag to be told apart by. A rate-limited round's own last command returned
    /// non-zero, so the round runner returns <see cref="Failed"/> and classifies it
    /// transient; a round whose tests failed returned zero from its own last command, so it
    /// is <see cref="Produced"/> and carries no class at all — and a <see cref="Produced"/>
    /// round is false here by the first clause rather than by any judgement about what went
    /// wrong inside it.
    /// </para>
    /// <para>
    /// That is the whole of the separation, and it is why the boundary belongs at the
    /// round's own exit code rather than at "did anything come back non-zero": the latter
    /// would make a failing test and a dead agent the same event, which is exactly what the
    /// design's own sentence warns against when it says a build that fails its tests is not
    /// a transient failure (DESIGN.md, ADR-0001).
    /// </para>
    /// </remarks>
    public bool IsRetryable =>
        Outcome == RoundOutcome.Failed && Failure == FailureClass.Transient;
}
