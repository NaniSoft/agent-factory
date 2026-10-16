namespace AgentFactory.Composition;

using AgentFactory.Application.Loop;
using AgentFactory.Application.Polling;
using AgentFactory.Application.Tickets;

/// <summary>
/// The factory's policy core: what a transition means, the loop that applies one at a
/// time, and intake (#81).
/// </summary>
/// <remarks>
/// The three are one group because they are the inside's judgement rather than the
/// process's plumbing — <see cref="TicketPolicy"/> is the rules both drivers call,
/// <see cref="Orchestrator"/> is the in-process state machine over them, and
/// <see cref="Poller"/> is intake — and because nothing here has a choice of
/// implementation to make. The adapters those three reach are registered in
/// <see cref="TheSeams"/>; how the machine is stepped is <see cref="TheDrivers"/>.
/// </remarks>
internal static class ThePolicyCore
{
    /// <summary>Registers the policy core, as the singletons the whole process shares.</summary>
    public static void AddTo(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // What each transition *means* is `TicketPolicy`, which both this loop and the
        // durable workflow call (#64): the loop is the in-process driver of the same rules
        // the workflow drives from its history, so the two cannot come to disagree about
        // a landing, a park or a merge.
        services.AddSingleton<TicketPolicy>();

        // The orchestrator is the factory's own policy: it knows the store, how to ask
        // for a round, how to ask for a merge, and the clock. It has no concept of a
        // container runtime — the agent seam it asks for a round through is a component
        // that has one — and the merge is one call on the one GitHub seam, so the loop
        // learns whether a change shipped without learning what a pull request is.
        services.AddSingleton<Orchestrator>();

        // The poller is intake: it reads the open issues of the projects being served and
        // records them as work items. Like the orchestrator it is stepped rather than
        // driven.
        services.AddSingleton<Poller>();
    }
}
