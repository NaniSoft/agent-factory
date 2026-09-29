namespace AgentFactory.Driving;

using AgentFactory.Loop;
using AgentFactory.Polling;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// The heartbeat: the one thing in the process that asks the machine to move on its own.
/// Every prior ticket recorded the same gap — a real agent, a real container, a real
/// deriver and a correct retry policy all exist, and nothing steps the loop or the poller
/// on a schedule, so in production a work item in Backlog only moved when a reviewer's
/// click asked for a step. This is that driver, and it is why the container budget had to
/// land with it: a timer with no budget behind it is a factory spending a container on
/// every issue of every project at once, which is exactly what #8 refused to add.
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
/// It respects the budget by not being the thing that enforces it. The driver has no
/// opinion about how many containers are running — it does not know, cannot know, and is
/// not asked. It asks the loop for a step, and the loop is the only component that counts
/// rounds, so a tick with a full budget applies a decision, a threshold merge or a landed
/// round if there is one and otherwise does nothing at all. The budget is a property of
/// the machine, not of its clock, which is what makes the driver safe to add rather than
/// something that has to be trusted.
/// </para>
/// <para>
/// The loop's own step is serialised by the step itself (one writer, one process,
/// ADR-0009), so two ticks cannot both read an empty in-flight set and start a third round
/// between them. That is the reason the budget cannot be exceeded by a driver at all, and
/// it is asserted rather than assumed.
/// </para>
/// </remarks>
public sealed class FactoryDriver : BackgroundService
{
    private readonly Poller _intake;
    private readonly Orchestrator _loop;
    private readonly ILogger<FactoryDriver> _logger;

    public FactoryDriver(Poller intake, Orchestrator loop, ILogger<FactoryDriver> logger)
    {
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
            await _intake.StepAsync();
            await _loop.SettleAsync();
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
            _logger.LogError(
                failure,
                "The heartbeat could not move the machine and will try again after {Interval}. "
                    + "Intake and the loop are each stepped; nothing has been changed by a tick that failed.",
                FactoryConstants.HeartbeatInterval);
        }
    }
}
