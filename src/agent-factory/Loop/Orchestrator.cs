namespace AgentFactory.Loop;

using AgentFactory.Clock;
using AgentFactory.GitHub;
using AgentFactory.Rounds;
using AgentFactory.WorkItems;
using Microsoft.Extensions.Logging;

/// <summary>
/// The orchestrator: a hand-rolled deterministic state machine over the work item store,
/// and the whole of the factory's policy about what runs next. It asks
/// <see cref="INOpenCode"/> for one round and receives one result, and it asks
/// <see cref="IGitHub"/> for one merge and learns whether it landed. It has no concept of
/// a container, an image, a process, or a pull request behind either call (ADR-0004,
/// ADR-0005, ADR-0006).
/// </summary>
/// <remarks>
/// The machine advances only when a caller steps it, and one step applies at most one
/// transition. A step never waits on a round: a round that has not come back is a round
/// that is still running, and the next step asks again. That is what makes the round
/// timeout a comparison against <see cref="IClock"/> rather than a timer, and what lets
/// the whole loop be tested without a sleep. A step does await the merge an approve asks
/// for, because merging is the transition that approve *is* rather than something asked
/// for earlier and collected later — a reviewer's click has to do something now.
/// </remarks>
public sealed class Orchestrator
{
    private readonly IWorkItemStore _store;
    private readonly INOpenCode _agent;
    private readonly IGitHub _github;
    private readonly IClock _clock;
    private readonly ILogger<Orchestrator> _logger;

    /// <summary>The round this process is inside, if it is inside one.</summary>
    private InFlight? _inFlight;

    public Orchestrator(
        IWorkItemStore store,
        INOpenCode agent,
        IGitHub github,
        IClock clock,
        ILogger<Orchestrator> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Applies at most one transition, and says what it made of it. A round in flight is
    /// the only thing the machine waits on; a work item waiting in Frontier is not,
    /// because starting it is a transition the next step can make. A decision a reviewer
    /// has made comes before everything else, because a reviewer is waiting on it.
    /// </summary>
    public async Task<StepResult> StepAsync()
    {
        if (_inFlight is { } run)
        {
            return LandIfTheRoundIsOver(run) ? StepResult.Moved : StepResult.Idle;
        }

        return await ApplyADecision()
            ?? (AcceptIntoFrontier() || StartARound() ? StepResult.Moved : StepResult.Idle);
    }

    /// <summary>
    /// Applies transitions until the machine has nothing left to apply. Stops at a round
    /// that has not come back, because a round that is running is not a transition — the
    /// caller steps again when it might have finished.
    /// </summary>
    public async Task SettleAsync()
    {
        while ((await StepAsync()).Applied)
        {
        }
    }

    /// <summary>
    /// The first decision the loop has not acted on, applied, or nothing at all if there
    /// is none. Returning nothing is what lets the step fall through to the rest of the
    /// machine; a decision that could not be applied is a result of its own, because it
    /// has an answer the reviewer has to read.
    /// </summary>
    private async Task<StepResult?> ApplyADecision()
    {
        foreach (var workItem in _store.List())
        {
            if (workItem.Swimlane != Swimlane.Review)
            {
                continue;
            }

            // Oldest first, because that is the order the reviewer made them in, and the
            // loop works through them in that order rather than picking and choosing.
            var pending = _store.Decisions(workItem.Id).FirstOrDefault(decision => decision.IsPending);
            if (pending is null)
            {
                continue;
            }

            return await Apply(workItem, pending);
        }

        return null;
    }

    private async Task<StepResult> Apply(WorkItem workItem, DecisionRecord decision)
    {
        switch (decision.Decision)
        {
            case Decision.Approve:
                return await Approve(workItem, decision);

            case Decision.RequestChanges:
                // Straight back into the build, which is where the next step starts the next
                // round with the reviewer's words as its brief. What eventually stops this
                // happening for ever is the round ceiling, which is its own ticket: until it
                // lands, a work item a reviewer keeps sending back keeps costing rounds.
                Land(workItem, decision, Swimlane.Frontier);
                return StepResult.Moved;

            case Decision.Reject:
                // Rejected is final, and this is the only edge that reaches it.
                Land(workItem, decision, Swimlane.Rejected);
                return StepResult.Moved;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(decision), decision.Decision, "there are only three decisions");
        }
    }

    /// <summary>
    /// The reviewer's approval, made into a merge. <c>Done</c> means merged — DESIGN.md
    /// says so, and it says approve triggers the auto-merge: the factory opens the pull
    /// request and merges it — so an approve is not complete until a merge has landed, and
    /// the loop asks the one GitHub seam to land it rather than deciding for itself that
    /// a change is shipped.
    /// </summary>
    /// <remarks>
    /// The call is awaited and the loop is not left holding a merge it has not seen the
    /// end of, because there is no heartbeat to collect one later: the reviewer's own
    /// click is the only thing that drives this machine between decisions. Nothing here
    /// sleeps or defers. A merge that could hang rather than fail is not a bound this
    /// ticket introduces — there is no timer here — and the bound the seam call needs is
    /// the merger's own (#10).
    /// </remarks>
    private async Task<StepResult> Approve(WorkItem workItem, DecisionRecord decision)
    {
        try
        {
            await _github.MergeAsync(workItem.RepoUrl, workItem.IssueNumber, CancellationToken.None);
        }
        catch (Exception refused)
        {
            _logger.LogWarning(
                refused,
                "The reviewer approved {Project}#{Issue} and the merge did not land: {Reason}. "
                    + "It is still in Review and nothing was shipped.",
                workItem.Project,
                workItem.IssueNumber,
                refused.Message);

            // The work item does not move, and the decision is recorded as applied to
            // Review: the loop has acted on the reviewer's approval and its answer was
            // "not yet". Applied rather than pending is the load-bearing choice. A pending
            // decision is retried on every step, which is retry — and backoff, and telling
            // a transient failure from a permanent one, which is the retry ticket's (#7);
            // an unbounded, unattended, unclassified merge attempt every time anything
            // steps the machine is not this ticket's fix to ship. Applied, the retry is a
            // human one: the work item is still in Review and the reviewer can approve it
            // again when the merger is there. Leaving it in Review rather than parking it
            // is the other half of that: where a failed merge *goes* — escalated, counted,
            // retried with backoff — is the exhaustion and escalation ticket's (#6), and
            // a lane this ticket invented would be the same policy decided twice.
            Land(workItem, decision, Swimlane.Review);

            return StepResult.Refused(
                $"{workItem.Project}#{workItem.IssueNumber} was approved, but the change was not merged: "
                    + $"{refused.Message}. It is still in Review, and nothing shipped.");
        }

        Land(workItem, decision, Swimlane.Done);
        return StepResult.Moved;
    }

    /// <summary>
    /// The loop's answer to a decision: the swimlane it means, and the record of which
    /// decision moved the work item, in one write.
    /// </summary>
    private void Land(WorkItem workItem, DecisionRecord decision, Swimlane to)
    {
        _store.ApplyDecision(decision.WorkItemId, decision.Sequence, to);

        _logger.LogInformation(
            "The reviewer {Decision} {Project}#{Issue}, which is now in {Swimlane}.",
            decision.Decision,
            workItem.Project,
            workItem.IssueNumber,
            Swimlanes.Label(to));
    }

    /// <summary>
    /// The last thing a reviewer said about this work item, which is what the next round
    /// is handed as its brief: the reviewer's words rather than a verdict. Empty when no
    /// reviewer has asked for changes, which is what a first round gets.
    /// </summary>
    private string BriefFor(Guid workItemId) => _store
        .Decisions(workItemId)
        .LastOrDefault(decision => decision.Decision == Decision.RequestChanges)
        ?.Feedback ?? string.Empty;

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

        // The round's number is the work item's rounds so far, plus this one. The brief
        // is the last thing a reviewer said about the work item, which is nothing at all
        // on a first round and the reviewer's own words on every round after one.
        var round = new Round(
            next.Id,
            next.Project,
            next.RepoUrl,
            next.IssueNumber,
            next.BaseBranch,
            BriefFor(next.Id));

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
