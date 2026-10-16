namespace AgentFactory.Infrastructure.WorkItems;

/// <summary>
/// One row of <c>pending_rounds</c>: the schema's shape rather than the port's, and the
/// whole of what reconciliation reads after a restart.
/// </summary>
internal sealed class PendingRoundRow
{
    public Guid WorkItemId { get; set; }

    public int RoundNumber { get; set; }

    public int Attempt { get; set; }

    public string Purpose { get; set; } = string.Empty;

    /// <summary>
    /// When the round was asked for. A `long` tick converter, because
    /// <c>ListPendingRounds</c> orders by it and the provider refuses `ORDER BY` over a
    /// `DateTimeOffset`.
    /// </summary>
    public DateTimeOffset AskedUtc { get; set; }

    /// <summary>When a retry may be asked for. Readable text: the wait is compared in the loop.</summary>
    public DateTimeOffset? RetryAfterUtc { get; set; }
}
