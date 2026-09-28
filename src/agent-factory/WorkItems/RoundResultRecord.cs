namespace AgentFactory.WorkItems;

using AgentFactory.Rounds;

/// <summary>
/// One round's result, as the factory kept it. The work item holds every round it has
/// run, not only the latest: a reviewer who has been through three rounds judges the
/// disagreement between them, and a result that overwrote its predecessor would take
/// that with it.
/// </summary>
public sealed record RoundResultRecord(
    Guid WorkItemId,
    int RoundNumber,
    RoundOutcome Outcome,
    string? ResultPayload,
    string? AgentNote,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc);
