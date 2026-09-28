namespace AgentFactory.Loop;

using AgentFactory.Clock;
using AgentFactory.Rounds;
using AgentFactory.WorkItems;
using Microsoft.Extensions.Logging;

/// <summary>
/// The orchestrator: a hand-rolled deterministic state machine over the work item store,
/// and the whole of the factory's policy about what runs next. It asks
/// <see cref="INOpenCode"/> for one round and receives one result. It has no concept of
/// a container, an image, or a process behind that call (ADR-0004, ADR-0005).
/// </summary>
/// <remarks>
/// The machine advances only when a caller steps it, and one step applies at most one
/// transition. A step never waits: a round that has not come back is a round that is still
/// running, and the next step asks again. That is what makes the round timeout a
/// comparison against <see cref="IClock"/> rather than a timer, and what lets the whole
/// loop be tested without a sleep.
/// </remarks>
public sealed class Orchestrator
{
    private readonly IWorkItemStore _store;
    private readonly INOpenCode _agent;
    private readonly IClock _clock;
    private readonly ILogger<Orchestrator> _logger;

    /// <summary>The round this process is inside, if it is inside one.</summary>
    private InFlight? _inFlight;

    public Orchestrator(IWorkItemStore store, INOpenCode agent, IClock clock, ILogger<Orchestrator> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Applies at most one transition, and says whether it applied one. A round in flight
    /// is the only thing the machine waits on; a work item waiting in Frontier is not,
    /// because starting it is a transition the next step can make.
    /// </summary>
    public bool Step() => _inFlight is { } run
        ? LandIfTheRoundIsOver(run)
        : AcceptIntoFrontier() || StartARound();

    /// <summary>
    /// Applies transitions until the machine has nothing left to apply. Stops at a round
    /// that has not come back, because a round that is running is not a transition — the
    /// caller steps again when it might have finished.
    /// </summary>
    public void Settle()
    {
        while (Step())
        {
        }
    }

    /// <summary>
    /// A work item is accepted out of Backlog when a slot is free. A slot is free when no
    /// round is in flight and nothing is waiting in Frontier; the budget behind that count
    /// is the concurrency ticket's business, and the shape here is the one it will
    /// generalise. Acceptance is automatic and is never gated on a human: the board is
    /// where a reviewer acts, through the three decisions, not by holding intake
    /// (ADR-0007).
    /// </summary>
    private bool AcceptIntoFrontier()
    {
        var workItems = _store.List();
        var next = workItems.FirstOrDefault(item => item.Swimlane == Swimlane.Backlog);
        if (next is null)
        {
            return false;
        }

        if (_inFlight is not null || workItems.Any(item => item.Swimlane == Swimlane.Frontier))
        {
            return false;
        }

        _store.Move(next.Id, Swimlane.Frontier);
        _logger.LogInformation(
            "Accepted {Project}#{Issue} into Frontier: a slot is free.",
            next.Project,
            next.IssueNumber);

        return true;
    }

    private bool StartARound()
    {
        var next = _store.List().FirstOrDefault(item => item.Swimlane == Swimlane.Frontier);
        if (next is null)
        {
            return false;
        }

        // The round's number is the work item's rounds so far, plus this one. Feedback is
        // empty on a first round; the reviewer's words are the decisions ticket's.
        var round = new Round(
            next.Id,
            next.Project,
            next.RepoUrl,
            next.IssueNumber,
            next.BaseBranch,
            Feedback: string.Empty);

        // The call is made and not awaited. The result is collected on a later step, which
        // is what keeps the loop from being blocked inside a container it knows nothing
        // about, and what makes the timeout a comparison against the clock. The token is
        // the round's own: ending the round ends the call.
        var inFlight = new InFlight(next.Id, next.RoundCount + 1, _clock.UtcNow);
        inFlight.Pending = AskForTheRound(round, inFlight.Token);

        _inFlight = inFlight;
        _store.Move(next.Id, Swimlane.InProgress);
        _logger.LogInformation(
            "Round {Round} of {Project}#{Issue} started.",
            inFlight.RoundNumber,
            next.Project,
            next.IssueNumber);

        return true;
    }

    /// <summary>
    /// Lands the round in flight if it is over: returned, or past the round timeout. A
    /// round that has done neither is still running, and the machine waits.
    /// </summary>
    private bool LandIfTheRoundIsOver(InFlight run)
    {
        var timedOut = _clock.UtcNow - run.StartedUtc >= FactoryConstants.RoundTimeout;

        if (!run.Pending.IsCompleted && !timedOut)
        {
            return false;
        }

        // The round is over either way, so the machine stops waiting on it and ends the
        // call it made. A round that produced a result is not cancelled — there is
        // nothing left running — but one that ran past the timeout is told to stop, so a
        // stuck session does not go on holding a worker container for ever.
        _inFlight = null;
        if (timedOut && !run.Pending.IsCompleted)
        {
            run.End();
        }

        run.Dispose();

        var result = timedOut && !run.Pending.IsCompleted ? TimedOut(run) : Read(run);

        var round = _store.RecordRound(
            run.WorkItemId,
            result.Outcome,
            result.ResultPayload,
            result.AgentNote,
            run.StartedUtc);

        // A round that came back with a result is what a reviewer judges, so it goes to
        // Review. A round that did not is not merged over: it parks in Escalated, which a
        // human can still merge or reject (ADR-0008).
        _store.Move(
            run.WorkItemId,
            result.Outcome == RoundOutcome.Produced ? Swimlane.Review : Swimlane.Escalated);

        _logger.LogInformation(
            "Round {Round} of work item {WorkItem} returned {Outcome}.",
            round.RoundNumber,
            round.WorkItemId,
            result.Outcome);

        return true;
    }

    /// <summary>
    /// Asks the agent for the round, and never lets the call escape: an agent that throws
    /// before it has a task to hand back is a round that came back without a result, and
    /// the machine has to survive that in order to record it. Classifying the failure is
    /// the retry ticket's work.
    /// </summary>
    private Task<RoundResult> AskForTheRound(Round round, CancellationToken cancellationToken)
    {
        try
        {
            return _agent.RunRoundAsync(round, cancellationToken);
        }
        catch (Exception failure)
        {
            return Task.FromException<RoundResult>(failure);
        }
    }

    private RoundResult TimedOut(InFlight run)
    {
        _logger.LogWarning(
            "Round {Round} of work item {WorkItem} ran past the {Timeout} round timeout.",
            run.RoundNumber,
            run.WorkItemId,
            FactoryConstants.RoundTimeout);

        return new RoundResult(RoundOutcome.TimedOut, null, null);
    }

    private RoundResult Read(InFlight run)
    {
        if (run.Pending.IsCompletedSuccessfully)
        {
            return run.Pending.Result;
        }

        _logger.LogWarning(
            run.Pending.Exception,
            "Round {Round} of work item {WorkItem} came back without a result.",
            run.RoundNumber,
            run.WorkItemId);

        return new RoundResult(RoundOutcome.Failed, null, null);
    }

    /// <summary>
    /// One round this process is inside. The pending call is kept rather than awaited
    /// inline, so that a round is a thing the machine can be asked about instead of a call
    /// it is blocked in.
    /// </summary>
    private sealed class InFlight(Guid workItemId, int roundNumber, DateTimeOffset startedUtc) : IDisposable
    {
        private readonly CancellationTokenSource _rounds = new();

        public Guid WorkItemId { get; } = workItemId;

        public int RoundNumber { get; } = roundNumber;

        public DateTimeOffset StartedUtc { get; } = startedUtc;

        public CancellationToken Token => _rounds.Token;

        public Task<RoundResult> Pending { get; set; } = Task.FromResult(
            new RoundResult(RoundOutcome.Failed, null, null));

        /// <summary>Ends a round the factory has stopped waiting for.</summary>
        public void End() => _rounds.Cancel();

        public void Dispose() => _rounds.Dispose();
    }
}
