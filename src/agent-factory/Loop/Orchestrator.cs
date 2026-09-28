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
    /// has made comes before everything else, because a reviewer who is present is
    /// waiting on it — and a reviewer who is not is what the feedback threshold is for,
    /// which comes next.
    /// </summary>
    /// <remarks>
    /// The order is the asymmetry, in the order it is read: a decision that was made
    /// beats a decision that was not. The merge of an ignored work item waits its turn
    /// like everything else does, and it waits for a round in flight too — bounded, since
    /// a round is bounded at the round timeout, and the wait makes the dangerous path
    /// later rather than earlier.
    /// </remarks>
    public async Task<StepResult> StepAsync()
    {
        if (_inFlight is { } run)
        {
            return LandIfTheRoundIsOver(run) ? StepResult.Moved : StepResult.Idle;
        }

        return await ApplyADecision()
            ?? await MergeWhatNobodyReviewed()
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
            if (!Swimlanes.Decidable.Contains(workItem.Swimlane))
            {
                continue;
            }

            // Oldest work item first, which is the order they were picked up in, and the
            // loop works through them in that order rather than picking and choosing.
            // Escalated is in the set as well as Review, because a human can still finish
            // a parked work item from the board (ADR-0008).
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
                // round with the reviewer's words as its brief — while a round is left to
                // spend. The count is of rounds run, so this is the comparison that
                // matters: a work item that has run the ceiling's worth has nothing left
                // to hand the change to, and sending it round again would start a fourth
                // round the ceiling exists to prevent.
                if (workItem.RoundCount >= FactoryConstants.RoundCeiling)
                {
                    return EscalatedForRunningOutOfRounds(workItem, decision);
                }

                Land(workItem, decision, Swimlane.Frontier);
                return StepResult.Moved;

            case Decision.Reject:
                // Rejected is final, and this is the only edge that reaches it — from
                // Review or from a parked work item, and a later step finds no decision to
                // apply about it because the store keeps none.
                Land(workItem, decision, Swimlane.Rejected);
                return StepResult.Moved;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(decision), decision.Decision, "there are only three decisions");
        }
    }

    /// <summary>
    /// A work item that has spent the ceiling, parked by a reviewer asking for changes
    /// again. It is not merged and it is not built again: the two ends of the ceiling
    /// are the rule, and ADR-0008 rules the exhausted one in favour of a human.
    /// </summary>
    private StepResult EscalatedForRunningOutOfRounds(WorkItem workItem, DecisionRecord decision)
    {
        _logger.LogWarning(
            "The reviewer asked for changes on {Project}#{Issue} and all {Ceiling} rounds are spent, "
                + "so it is parked for a human rather than built again or merged over that objection.",
            workItem.Project,
            workItem.IssueNumber,
            FactoryConstants.RoundCeiling);

        Land(workItem, decision, Swimlane.Escalated);
        return StepResult.Moved;
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
    ///
    /// A merge that did not land parks the work item in Escalated, and which lane that is
    /// was this ticket's judgement to make: #20 left it in Review on purpose, so that the
    /// policy would be decided once and here. The argument runs: a factory that cannot
    /// ship what a human approved has failed, and a failure is what Escalated means, in
    /// the design's dead-letter language and in CONTEXT.md's own cause for the state.
    ///
    /// Parking is the stronger half of it. Leaving the work item in Review would leave it
    /// inside the feedback threshold's reach, so every 48 hours the factory would try
    /// again to merge a change whose merge it already knows fails — an unattended,
    /// unbounded, unclassified retry of the one operation in this system that the
    /// repository cannot take back by ignoring it. Escalation takes it out of the
    /// timeout's window altogether, which is what keeps the more dangerous path governed
    /// by something. Nothing is lost by parking it, because Escalated is a parking state
    /// rather than a grave: a human can still merge it from the board, in one click, with
    /// the refusal in front of them rather than a guess.
    ///
    /// The loop still does not retry it — how many attempts, how soon, and telling a
    /// transient failure from a permanent one is #7's — so the one attempt a reviewer can
    /// see and make again themselves is the whole of it. The decision is recorded applied
    /// rather than pending, so nothing re-applies it on a later step or across a restart.
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
                    + "It is parked in Escalated, where a human can finish it, and nothing shipped.",
                workItem.Project,
                workItem.IssueNumber,
                refused.Message);

            // Parked, and applied rather than pending. Applied because a pending decision
            // is re-applied on every step and across every restart, which is retry; retry
            // is #7's, and an unclassified one would be worse than the single attempt a
            // reviewer can see and make again themselves.
            Land(workItem, decision, Swimlane.Escalated);

            return StepResult.Refused(
                $"{workItem.Project}#{workItem.IssueNumber} was approved, but the change was not merged: "
                    + $"{refused.Message}. It is parked in Escalated, where a human can merge it, and nothing shipped.");
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
    /// The feedback timeout: a work item left in Review past
    /// <see cref="FactoryConstants.FeedbackThreshold"/> is merged, because a pipeline held
    /// by a reviewer who is not watching is a pipeline that has stopped. The design says
    /// this plainly rather than inferring it, and the threshold is rendered on the board
    /// so it can be governed.
    /// </summary>
    /// <remarks>
    /// This is a merge and not a shortcut round Done. It goes through the same
    /// <see cref="IGitHub"/> seam an approve does and obeys the same rule, so a Done here
    /// is a claim about a repository rather than about a clock: with no merger behind the
    /// seam the timeout cannot complete either, and a work item left for two days must not
    /// report a merge that did not happen. It is also exactly one attempt, because the work
    /// item leaves Review the moment it lands or parks — Done is outside the threshold's
    /// reach and so is Escalated, which is what stops an unattended loop of merge attempts
    /// that nobody classified as transient (#7).
    ///
    /// It is a comparison against <see cref="IClock"/> and not a timer, and it is measured
    /// from when the work item entered Review rather than from when it was last written to.
    /// So a test drives it by advancing time, nothing waits, and a work item sent back and
    /// reviewed again gets a full threshold rather than inheriting the last one's remainder.
    /// </remarks>
    private async Task<StepResult?> MergeWhatNobodyReviewed()
    {
        var ignored = _store.List().FirstOrDefault(WaitedLongEnough);
        if (ignored is null)
        {
            return null;
        }

        _logger.LogWarning(
            "{Project}#{Issue} has been in Review since {Since} and nobody reviewed it in {Threshold}, "
                + "so the factory is merging it. Ignoring a work item merges it; that is deliberate.",
            ignored.Project,
            ignored.IssueNumber,
            ignored.ReviewStartedUtc,
            FactoryConstants.FeedbackThresholdText);

        try
        {
            await _github.MergeAsync(ignored.RepoUrl, ignored.IssueNumber, CancellationToken.None);
        }
        catch (Exception refused)
        {
            _logger.LogWarning(
                refused,
                "{Project}#{Issue} was left past the {Threshold} and the merge did not land: {Reason}. "
                    + "Nothing shipped, and it is parked where a human can finish it.",
                ignored.Project,
                ignored.IssueNumber,
                FactoryConstants.FeedbackThresholdText,
                refused.Message);

            // Parked for the same reason a failed approval is parked: a merge that did not
            // land is a failure, and Escalated is what a failure is. Parked rather than
            // left in Review also takes it out of the threshold's reach, so an unattended
            // retry every 48 hours cannot happen.
            _store.Move(ignored.Id, Swimlane.Escalated);

            return StepResult.Refused(
                $"{ignored.Project}#{ignored.IssueNumber} was left in Review past the "
                    + $"{FactoryConstants.FeedbackThresholdText} and the change was not merged: {refused.Message}. "
                    + "Nothing shipped. It is parked in Escalated, where a human can merge it, and nothing further "
                    + "will be attempted on its own.");
        }

        _store.Move(ignored.Id, Swimlane.Done);
        return StepResult.Moved;
    }

    /// <summary>
    /// Whether a work item is waiting in Review for longer than the feedback threshold
    /// allows. A work item that is not in Review is never overdue however long it has been
    /// there, which is what keeps the two ends of the asymmetry apart: silence merges, and
    /// an objection — whether it arrived as spent rounds, a failed build, a failed merge
    /// or a decline — parks or stays final, and is never merged over (ADR-0008).
    /// </summary>
    private bool WaitedLongEnough(WorkItem workItem) =>
        workItem.Swimlane == Swimlane.Review
        && workItem.ReviewStartedUtc is { } since
        && _clock.UtcNow - since >= FactoryConstants.FeedbackThreshold;

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
