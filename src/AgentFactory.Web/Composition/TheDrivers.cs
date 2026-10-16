namespace AgentFactory.Composition;

using AgentFactory.Application;
using AgentFactory.Application.Driving;
using AgentFactory.Application.Durability;
using AgentFactory.Infrastructure.Durability;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// How the machine is stepped: the heartbeat, and the durable workflow when a Temporal
/// endpoint is configured (#81).
/// </summary>
/// <remarks>
/// One policy core, two drivers — the architecture document's own shape. The heartbeat is
/// registered always and started only when a caller asks for a moving machine; the durable
/// path is registered only when there is an endpoint to reach it at, and the starter that
/// is not there answers every call as a no-op. Which of the two is doing the stepping is a
/// property of configuration, not of the endpoints or the routes.
/// </remarks>
internal static class TheDrivers
{
    /// <summary>
    /// Registers the heartbeat and, when a Temporal endpoint is configured, the workflow
    /// that supersedes it.
    /// </summary>
    /// <param name="drivingTheMachine">
    /// Whether the heartbeat runs as a hosted service. The test host passes false and steps
    /// the machine by hand, because a live five-second tick running alongside a test that
    /// asserts on the state of the machine would make every test in the suite a race
    /// against a timer. The driver is registered either way, so a test can tick it.
    /// </param>
    public static void AddTo(IServiceCollection services, FactoryOptions options, bool drivingTheMachine)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        // The heartbeat, and the last gap between this factory and a moving one. Every
        // prior ticket recorded the same thing: a real agent, a real deriver and a bounded
        // retry policy all exist, and nothing steps the machine on a schedule, so a work
        // item in Backlog only moved when a reviewer's click asked for a step. The driver
        // is that step, and it is safe to add now rather than earlier for exactly one
        // reason: the round concurrency landed with it. A timer with no ceiling behind it
        // is a factory spending a round on every issue of every project at once, which is
        // what #8 refused.
        //
        // It is a hosted service, so the host starts and stops it, and it asks the loop for
        // a step rather than deciding anything itself — the concurrency is enforced where
        // the rounds are counted, in the loop, so a tick at the ceiling does nothing.
        services.AddSingleton<FactoryDriver>();
        if (drivingTheMachine)
        {
            services.AddHostedService(provider => provider.GetRequiredService<FactoryDriver>());
        }

        // The durable workflow, when a Temporal endpoint is configured. One workflow per
        // ticket: its history is the record of where the ticket was, a round in flight
        // survives a restart of this process, and the board's clicks reach the workflow as
        // updates. The hosted service hosts the worker and reconnects on its own, so a
        // Temporal outage is a degraded board rather than a dead factory. Without an
        // endpoint the factory runs in store-only mode exactly as it always did, and the
        // starter that is not there answers every call as a no-op — which is also what a
        // test host gets, unless it substitutes its own.
        if (!string.IsNullOrWhiteSpace(options.TemporalEndpoint))
        {
            services.AddSingleton<ITicketStarter, TemporalTickets>();
            services.AddHostedService(provider =>
                (TemporalTickets)provider.GetRequiredService<ITicketStarter>());
        }
        else
        {
            services.TryAddSingleton<ITicketStarter, NullTicketStarter>();
        }
    }
}
