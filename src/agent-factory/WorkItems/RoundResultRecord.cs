namespace AgentFactory.WorkItems;

using AgentFactory.Failures;
using AgentFactory.Rounds;

/// <summary>
/// One round's result, as the factory kept it. The work item holds every round it has
/// run, not only the latest: a reviewer who has been through three rounds judges the
/// disagreement between them, and a result that overwrote its predecessor would take
/// that with it.
/// </summary>
/// <remarks>
/// <paramref name="Attempts"/> is how many times the factory asked for this round, which
/// is not how many rounds it is: a round that failed transiently twice and then produced a
/// result is still round one, and the ceiling is compared against rounds rather than
/// attempts. Both are kept because a reviewer reading a result wants to know that the
/// container fought the factory a little on the way to producing it, and because a retry
/// policy nobody can count is a retry policy nobody can hold to.
/// </remarks>
/// <param name="Failure">
/// The round runner's own classification of a failure the round did not survive, and null
/// when the round came back with a result at all. Kept with the round so the board can
/// say why a round was asked for more than once — or, for a build that failed its own
/// tests, that it was not asked for twice.
/// </param>
/// <param name="Log">
/// The end of what the round said, and the whole of what a reviewer has when the payload
/// is not a readable result. It is the tail rather than the log: a ninety-minute build's
/// output does not belong in a database row, and the whole of it went to the factory's
/// own logging as the round ran (story 28, story 29).
/// </param>
/// <param name="Diff">
/// The round's change, as the host generated it from the tree the round left. Kept beside
/// the payload rather than inside it, and that separation is the point: the payload is
/// what the container recorded about itself, this is <c>git diff</c> run on the host
/// against the lifted tree, and a board that showed only the first would show a bounded
/// prefix of the change on exactly the rounds a reviewer most needs to see. It is kept
/// even for a round that produced no readable payload, because a round whose result could
/// not be read is a round whose tree is the whole of what it left (ADR-0006, ADR-0011).
/// </param>
public sealed record RoundResultRecord(
    Guid WorkItemId,
    int RoundNumber,
    RoundOutcome Outcome,
    string? ResultPayload,
    string? AgentNote,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    int Attempts = 1,
    FailureClass? Failure = null,
    string? Log = null,
    Results.HostDiff? Diff = null)
{
    /// <summary>
    /// Whether the round has a change the board can show at all. An empty diff still
    /// counts: a round that changed nothing is a fact a reviewer is entitled to be told,
    /// and it is a different fact from a round whose tree could not be read.
    /// </summary>
    public bool HasADiff => Diff is not null;
}
