namespace AgentFactory.Observability;

using Microsoft.Extensions.Logging;

/// <summary>
/// The identity a record carries because of where it was written, rather than because its
/// message said so. This is how "every log record carries the work item it belongs to"
/// holds for a component that was handed no work item at all: the result deriver, the
/// container runtime and the Docker CLI are three components that know a round is
/// happening and nothing about which work item it is an attempt at, and their records
/// carry the answer anyway because the round is inside a scope the loop opened (ADR-0004,
/// ADR-0011).
/// </summary>
/// <remarks>
/// <para>
/// The alternative was to thread a work item id into three more constructors, one of which
/// <c>PolicyTests</c> already pins to a single <c>ILogger</c>, and it is worth saying why
/// that is the worse shape: an id passed down a call chain is an id somebody can forget to
/// pass, and a record without one is exactly the record that cannot be attributed. A scope
/// is ambient, so it is right for every record written inside it without any of them having
/// to know it exists — and it is what lets the deriver keep taking nothing but a logger
/// and still say which work item it was reading for.
/// </para>
/// <para>
/// The four keys are prefixed on purpose. A record's own message may already name the
/// project and the issue — most of them do, because a line a human reads should say which
/// repository it is about — and a JSON sink that merged a scope key and a message key of
/// the same name would emit the same field twice. So the scope is the <em>machine</em>
/// identity and the message is the <em>human</em> one, and they are named so they cannot
/// collide.
/// </para>
/// <para>
/// Scopes nest, and the round number rides on the outer one. The loop opens a scope with
/// the round number around the call it makes for a round; the round runner opens its own
/// with the identity it was handed, inside it. A record the deriver writes an hour into a
/// ninety-minute round therefore carries the work item <em>and</em> the round it belongs
/// to, and the round runner is not one argument closer to knowing either of them than
/// <see cref="Rounds.Round"/> already is.
/// </para>
/// </remarks>
public static class WorkItemScope
{
    /// <summary>The work item's own identity, and the key every record about it is found by.</summary>
    public const string WorkItemIdKey = "WorkItemId";

    /// <summary>The project's name, as its project file spells it.</summary>
    public const string ProjectKey = "WorkItemProject";

    /// <summary>The issue number, inside that project.</summary>
    public const string IssueKey = "WorkItemIssue";

    /// <summary>The round number, when the record was written inside one.</summary>
    public const string RoundKey = "WorkItemRound";

    /// <summary>
    /// Every key a work item scope can carry, in the order it is put on a record. A test
    /// asserts this is the whole set, so that a fourth kind of ambient identity cannot be
    /// added without somebody meeting it.
    /// </summary>
    public static readonly IReadOnlyList<string> Keys = [WorkItemIdKey, ProjectKey, IssueKey, RoundKey];

    /// <summary>
    /// Puts the work item's identity on every record written until the returned scope is
    /// disposed. The round number is optional because a record about a work item is often
    /// written when no round is running — a decision, a refusal, a work item being accepted
    /// into the waiting room — and a round number invented for those would be worse than
    /// none.
    /// </summary>
    public static IDisposable? ForWorkItem(
        this ILogger logger,
        Guid workItemId,
        string project,
        int issueNumber,
        int? roundNumber = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [WorkItemIdKey] = workItemId,
            [ProjectKey] = project,
            [IssueKey] = issueNumber,
        };

        if (roundNumber is { } round)
        {
            fields[RoundKey] = round;
        }

        return logger.BeginScope(fields);
    }
}
