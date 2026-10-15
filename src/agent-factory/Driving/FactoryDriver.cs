namespace AgentFactory.Driving;

using AgentFactory.Durability;
using AgentFactory.Loop;
using AgentFactory.Polling;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// The heartbeat: the one thing in the process that asks the machine to move on its own.
/// Every prior ticket recorded the same gap — a real agent, a real deriver and a correct
/// retry policy all exist, and nothing steps the loop or the poller on a schedule, so in
/// production a work item in Backlog only moved when a reviewer's click asked for a step.
/// This is that driver, and it is why the round concurrency had to land with it: a timer
/// with no ceiling behind it is a factory spending a round on every issue of every project
/// at once, which is exactly what #8 refused to add.
/// </summary>
/// <remarks>
/// <para>
/// It is a <em>tick</em>, not a wait. Each tick asks intake for one project's turn and the
/// loop to apply whatever it can, and then returns. Nothing is held up: the loop applies
/// at most one transition per step, so the tick's work is bounded by what the machine has
/// to apply rather than by a clock, and the interval below is the only thing in the process
/// that has ever waited for anything.
/// </para>
/// <para>
/// When a Temporal endpoint is configured, the loop is no longer stepped: the per-ticket
/// workflow owns the control flow and drives itself from Temporal's history, and two
/// drivers over the same ticket would be two factories asking for the same round. The
/// driver keeps stepping intake (which starts the workflows); the loop it leaves alone,
/// and the loop's own step machinery stays in the codebase for the store-only mode that
/// runs without a Temporal endpoint.
/// </para>
/// </remarks>
public sealed class FactoryDriver : BackgroundService
{
    private readonly ITicketStarter _tickets;
    private readonly Poller _intake;
    private readonly Orchestrator _loop;
    private readonly ILogger<FactoryDriver> _logger;

    public FactoryDriver(
        ITicketStarter tickets,
        Poller intake,
        Orchestrator loop,
        ILogger<FactoryDriver> logger)
    {
        _tickets = tickets ?? throw new ArgumentNullException(nameof(tickets));
        _intake = intake ?? throw new ArgumentNullException(nameof(intake));
        _loop = loop ?? throw new ArgumentNullException(nameof(loop));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// The scheduled tick. It is the <em>only</em> <c>Task.Delay</c> in the factory, and
    /// the only place anything waits: the poller's interval, the round timeout, the retry
    /// backoffs and the feedback threshold are all <see cref="TimeSpan"/>s compared
    /// against <c>IClock</c>, and this is the one line that has to find out for itself
    /// that time passed. <c>PolicyTests</c> names this method as the single permitted
    /// exception rather than allowing waits in general, so a second one anywhere — or a
    /// timer smuggled in under another name — fails the suite.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Not a spin and not a fire-and-forget: the loop below is the whole driver, and it
        // runs until the host asks it to stop. A tick that throws is caught inside TickAsync
        // and logged, because a driver that died on the first bad repository would leave a
        // factory that looks wired and does nothing — which is the failure this class
        // exists to end.
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(FactoryConstants.HeartbeatInterval, stoppingToken);

            await TickAsync();
        }
    }

    /// <summary>
    /// One heartbeat: intake takes a turn if the poll interval says one is due, and the
    /// loop applies everything it can until it has nothing left. Both are stepped, and
    /// both decide for themselves whether now is the moment — which is why the driver
    /// never has to know how long a pass or a backoff is.
    /// </summary>
    /// <remarks>
    /// This is a separate method from <see cref="ExecuteAsync"/> on purpose: it is what a
    /// test calls to make the machine move without a clock of its own and without a sleep,
    /// and it is the whole of what a tick does.
    /// </remarks>
    public async Task TickAsync()
    {
        try
        {
            // Intake always steps: with a Temporal endpoint configured it is also what
            // starts the workflows, by re-offering every waiting ticket each pass.
            await _intake.StepAsync();

            // The loop is store-only mode's driver. When the tickets are workflow-owned,
            // two drivers over the same ticket would be two factories asking for the same
            // round, so the loop is left alone and the workflow drives from its own
            // history.
            if (!_tickets.Configured)
            {
                await _loop.SettleAsync();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure)
        {
            // Contained to the tick. The poller already contains a repository that cannot
            // be read to its own turn and the loop contains a merge that did not land, so
            // what reaches here is something neither of them expected — and the next tick
            // is five seconds away, so stopping the factory for it would be the larger
            // failure. It is logged loudly rather than swallowed, because a driver failing
            // on every tick is a factory that has stopped and says nothing.
            //
            // **The severity was reconsidered for the observability work and stayed at
            // Error, for a reason worth stating rather than asserting.** A tick failing *is*
            // ordinary in one specific sense: a repository that is down, a merge that was
            // refused and a round that failed are all contained below this method, and none
            // of them reaches here. What is left is the store — a SQLite write that failed,
            // a locked file, a full disk, a corrupt database — and a bug in the loop or the
            // poller. Both are faults an operator has to act on rather than rhythms to
            // watch, and `Error` is the level every log filter keys on, so downgrading it
            // would leave the process with no level at all meaning "the machine is broken",
            // which is the state this class exists to end.
            //
            // The severity rule the process now uses, in one place: `Information` is the
            // machine moving — a transition, a round landing, a decision applied, intake;
            // `Warning` is something the policy anticipated and contained — a transient
            // failure, a backoff, a refused merge, a missing credential, a refused project
            // file; `Error` is something no policy anticipated; and `Debug` is the round's
            // own output, which is unbounded and belongs to a reader debugging it rather
            // than to a deployment keeping logs.
            _logger.LogError(
                failure,
                "The heartbeat could not move the machine and will try again after {Interval}. "
                    + "Intake and the loop are each stepped; nothing has been changed by a tick that failed.",
                FactoryConstants.HeartbeatInterval);
        }
    }
}
