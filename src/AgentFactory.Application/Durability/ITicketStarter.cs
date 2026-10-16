namespace AgentFactory.Application.Durability;

/// <summary>
/// The connection to durable execution: starts one workflow per ticket, delivers the
/// board's accept and decisions to it as updates, and hosts the worker that runs the
/// ticket's activities. Present only when a Temporal endpoint is configured; without one
/// the factory runs in store-only mode exactly as it always did.
/// </summary>
/// <remarks>
/// <para>
/// A port, declared inside for the same reason the other eight are: the policy core — the
/// poller here — says it needs tickets started without knowing that a workflow engine is
/// how that happens. The implementations (<c>TemporalTickets</c>, <c>NullTicketStarter</c>)
/// stay with the rest of the composition root until the Durability slice moves.
/// </para>
/// <para>
/// The worker's activity concurrency is the round concurrency — the same
/// <c>FactoryConstants.RoundConcurrency</c> the loop counted — because at most that many
/// round activities may run at once across every ticket, which is what the concurrency
/// always meant in store-only mode: a bound on rounds, not on tickets.
/// </para>
/// </remarks>
public interface ITicketStarter
{
    /// <summary>Whether a Temporal endpoint is configured and tickets are workflow-owned.</summary>
    bool Configured { get; }

    /// <summary>Starts (or confirms) the ticket's workflow. Safe to call again: one ticket is one workflow id.</summary>
    Task StartTicketAsync(Guid workItemId);

    /// <summary>
    /// Delivers the acceptance click. Returns false with no refusal when the ticket has no
    /// workflow (a ticket from before Temporal), which the caller answers from the store as
    /// it always did.
    /// </summary>
    Task<(bool Handled, bool Applied, string? Refusal)> AcceptAsync(Guid workItemId);

    /// <summary>
    /// Delivers a reviewer's decision. Same no-workflow rule as the acceptance: the caller
    /// falls back to the store path for a ticket the workflow never owned.
    /// </summary>
    Task<(bool Handled, bool Applied, string? Refusal)> DecideAsync(
        Guid workItemId, string decision, string? feedback);
}
