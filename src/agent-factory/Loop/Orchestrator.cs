namespace AgentFactory.Loop;

using AgentFactory.Clock;
using AgentFactory.Failures;
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
            ?? await RetryAParkedMerge()
            ?? (AcceptIntoFrontier() || StartARound() ? StepResult.Moved : StepResult.Idle);
    }

    /// <summary>
    /// Applies transitions until the machine has nothing left to apply. Stops at a round
    /// that has not come back, because a round that is running is not a transition — the
    /// caller steps again when it might have finished. A round waiting out its retry
    /// backoff stops it too, for the same reason and with the same consequence: the wait is
    /// ended by the caller's clock rather than by anything in here.
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
            ParkAMergeThatDidNotLand(workItem, refused, "the reviewer approved it");

            // Parked, and applied rather than pending. Applied because a pending decision
            // is re-applied on every step and across every restart, which is retry; retry
            // is bounded here, by the work item's own record of how many attempts it has
            // had, and a pending decision would be unbounded.
            Land(workItem, decision, Swimlane.Escalated);

            return StepResult.Refused(
                $"{workItem.Project}#{workItem.IssueNumber} was approved, but the change was not merged: "
                    + $"{refused.Message}. It is parked in Escalated, where a human can merge it, and nothing shipped.");
        }

        Land(workItem, decision, Swimlane.Done);
        return StepResult.Moved;
    }

    /// <summary>
    /// A merge that did not land, on the record: one more attempt counted, and a time
    /// after which there may be one more, or none at all. The classification is the seam's
    /// and the gate is this policy's, and they are the only two things that decide whether
    /// a merge is ever tried again.
    /// </summary>
    /// <remarks>
    /// The count goes on the work item rather than in this process's memory, and that is
    /// the whole of what keeps the retry bounded: a restart does not forget how many
    /// attempts a merge has had, so it cannot hand a merge the repository has already
    /// refused a fresh budget, and neither can a second pass through this method.
    /// </remarks>
    private void ParkAMergeThatDidNotLand(WorkItem workItem, Exception refused, string how)
    {
        // Read here and nowhere else, and read off what the seam declared rather than off
        // what it said: an exception that did not classify itself is not retried, which is
        // the same rule a round's classification follows and for the same reason. A
        // permanent failure is this attempt and no more, ever.
        var failure = Failures.Classify(refused);
        var attempts = workItem.MergeAttempts + 1;
        var again = failure == FailureClass.Transient && attempts < FactoryConstants.TransientRetryAttempts;

        _store.RecordMergeFailure(
            workItem.Id,
            failure,
            again ? _clock.UtcNow + FactoryConstants.RetryBackoff(attempts) : null);

        _logger.LogWarning(
            refused,
            "{Project}#{Issue} — {How} — and the merge did not land: {Reason}. Attempt {Attempt} of {Ceiling}. "
                + "Nothing shipped, it is parked in Escalated, and {WhatNext}.",
            workItem.Project,
            workItem.IssueNumber,
            how,
            refused.Message,
            attempts,
            FactoryConstants.TransientRetryAttempts,
            again
                ? $"the factory will try once more after {FactoryConstants.RetryBackoff(attempts).TotalSeconds:0} seconds"
                : failure == FailureClass.Transient
                    ? "that was the last attempt the factory makes on its own"
                    : "the factory read that as a permanent failure and will not try again");
    }

    /// <summary>
    /// One more attempt at a merge that failed transiently and is still within its budget.
    ///
    /// <para>
    /// This is the deliberate widening of Escalated that the parking decision was waiting
    /// for. Parking exists so that a merge the repository will not take is not retried
    /// unattended every 48 hours for ever; this is how a merge the repository <em>would</em>
    /// take — a daemon that was restarting, an API that was refusing — gets a second and
    /// third try without putting it back where the feedback threshold could reach it.
    /// </para>
    /// <para>
    /// It is not the loop #6 refused, and the differences are the argument. Those
    /// attempts were unbounded, unclassified, and paced by a 48-hour threshold, on a work
    /// item sitting in Review where the threshold could keep finding it. These are bounded
    /// at three, classified — a permanent failure is not retried at all — paced by a
    /// backoff that grows, counted on the work item where a restart cannot forget it, and
    /// available only to a work item that has a merge failure of its own. A work item
    /// parked by a failed build, by spent rounds or by a human decline has no such record,
    /// so nothing here can merge it: this path is reachable only by a work item the
    /// factory has already tried and failed to ship.
    /// </para>
    /// <para>
    /// It is a transition the loop makes and not a decision a reviewer made, so the board's
    /// write path does not grow: a parked work item still offers approve and reject, and
    /// nothing else (ADR-0008). A step that makes an attempt and is refused again is not a
    /// refusal for the board to render — there is no reviewer holding a response — so it is
    /// a move and a log line, and the card says where the count got to.
    /// </para>
    /// </summary>
    private async Task<StepResult?> RetryAParkedMerge()
    {
        var parked = _store.List().FirstOrDefault(NeedsAnotherMergeAttempt);
        if (parked is null)
        {
            return null;
        }

        try
        {
            await _github.MergeAsync(parked.RepoUrl, parked.IssueNumber, CancellationToken.None);
        }
        catch (Exception refused)
        {
            ParkAMergeThatDidNotLand(parked, refused, "the factory is retrying a merge that failed transiently");

            return StepResult.Moved;
        }

        _store.Move(parked.Id, Swimlane.Done);
        _logger.LogInformation(
            "The merge for {Project}#{Issue} landed on attempt {Attempt}, so it is Done.",
            parked.Project,
            parked.IssueNumber,
            parked.MergeAttempts + 1);

        return StepResult.Moved;
    }

    /// <summary>
    /// Whether a work item is parked with a merge failure of its own that is both still
    /// within its budget and past its wait.
    /// </summary>
    /// <remarks>
    /// The lane is the least interesting of the three conditions, and the one that would be
    /// the mistake to lean on: what makes this reachable is the record of a failed merge,
    /// not the lane that failure parked it in. <c>MergeRetryAfterUtc</c> is the record —
    /// <c>RecordMergeFailure</c> sets the two together and a permanent failure sets neither,
    /// so a work item with no gate has nothing owed to it whatever lane it is in. There is
    /// deliberately no <c>MergeAttempts &gt; 0</c> check as well: it would say the same
    /// thing twice, and a redundant guard in a predicate reads as a load-bearing one.
    /// </remarks>
    private bool NeedsAnotherMergeAttempt(WorkItem workItem) =>
        workItem.Swimlane == Swimlane.Escalated
        && workItem.MergeAttempts < FactoryConstants.TransientRetryAttempts
        && workItem.MergeRetryAfterUtc is { } after
        && _clock.UtcNow >= after;

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
            // Parked for the same reason a failed approval is parked: a merge that did not
            // land is a failure, and Escalated is what a failure is. Parked rather than
            // left in Review also takes it out of the threshold's reach — and the widening
            // of Escalated does not bring it back, because a work item the loop merges
            // unattended is not one it merges because the threshold expired again.
            ParkAMergeThatDidNotLand(ignored, refused, "it was left past the feedback threshold");

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

        // The round's number is the work item's rounds so far, plus this one. The brief is
        // the last thing a reviewer said about the work item, which is nothing at all on a
        // first round and the reviewer's own words on every round after one.
        var round = RoundFor(next.Id);

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
    /// round that has done neither is still running, and the machine waits. A round that
    /// has failed transiently and is waiting out its backoff is over in the only sense
    /// that matters — it is not running — but it has not ended, and it goes back through
    /// here to be asked for again.
    /// </summary>
    private bool LandIfTheRoundIsOver(InFlight run)
    {
        if (run.IsWaitingToRetry)
        {
            return TryTheRetry(run);
        }

        var timedOut = _clock.UtcNow - run.StartedUtc >= FactoryConstants.RoundTimeout;

        if (!run.Pending.IsCompleted && !timedOut)
        {
            return false;
        }

        // A round that produced a result is not cancelled — there is nothing left running —
        // but one that ran past the timeout is told to stop, so a stuck session does not go
        // on holding a worker container for ever.
        if (timedOut && !run.Pending.IsCompleted)
        {
            run.End();
        }

        var result = timedOut && !run.Pending.IsCompleted ? TimedOut(run) : Read(run);

        // The retry policy, in the two lines that are all of it: a round that says it is
        // transient and has attempts left does not end, and everything else does. The
        // comparison is `IsRetryable` rather than anything read here, because a round that
        // came back with a result — a build whose own tests failed, most of all — has no
        // classification to be transient about.
        if (result.IsRetryable && run.Attempts < FactoryConstants.TransientRetryAttempts)
        {
            return WaitOutTheBackoff(run);
        }

        // The round is over, and now the in-flight state can go: the container is gone, the
        // round's token has nothing left to cancel, and a work item in Escalated is not
        // holding a slot.
        _inFlight = null;
        run.Dispose();

        var round = _store.RecordRound(
            run.WorkItemId,
            result.Outcome,
            result.ResultPayload,
            result.AgentNote,
            run.StartedUtc,
            run.Attempts,
            result.Failure,
            result.Log);

        // A round that came back with a result is what a reviewer judges, so it goes to
        // Review. A round that did not is not merged over: it parks in Escalated, which a
        // human can still merge or reject (ADR-0008). That is the answer for a permanent
        // failure, for a timeout, and for a transient one that has spent its attempts.
        _store.Move(
            run.WorkItemId,
            result.Outcome == RoundOutcome.Produced ? Swimlane.Review : Swimlane.Escalated);

        _logger.LogInformation(
            "Round {Round} of work item {WorkItem} returned {Outcome} after {Attempts} attempt(s).",
            round.RoundNumber,
            round.WorkItemId,
            result.Outcome,
            round.Attempts);

        return true;
    }

    /// <summary>
    /// A round that failed transiently and has attempts left does not end: it is asked for
    /// again, once its backoff has passed, as the same round. Not a new round, because a
    /// container that would not start is not an attempt at building anything — charging it
    /// to the ceiling would spend a reviewer's budget on the factory's own infrastructure
    /// (ADR-0001). The work item stays in In Progress and keeps its slot, because a round
    /// that has not ended has not released one.
    /// </summary>
    private bool WaitOutTheBackoff(InFlight run)
    {
        var backoff = FactoryConstants.RetryBackoff(run.Attempts);
        run.WaitForRetryUntil(_clock.UtcNow + backoff);

        _logger.LogWarning(
            "Round {Round} of work item {WorkItem} failed transiently on attempt {Attempt} of {Ceiling}. "
                + "It is asked for again after {Backoff}, and nothing waits here for it.",
            run.RoundNumber,
            run.WorkItemId,
            run.Attempts,
            FactoryConstants.TransientRetryAttempts,
            backoff);

        return true;
    }

    /// <summary>
    /// Asks for the round again, if the wait has passed. A step asked sooner is Idle and
    /// does nothing at all: the backoff is a comparison against the clock and not a wait,
    /// which is what lets three attempts with backoff be tested in microseconds and keeps
    /// a sleep out of the loop.
    /// </summary>
    private bool TryTheRetry(InFlight run)
    {
        if (_clock.UtcNow < run.RetryAfterUtc)
        {
            return false;
        }

        run.Attempts++;
        run.TakeTheRetryGate();
        run.Pending = AskForTheRound(RoundFor(run.WorkItemId), run.Token);

        _logger.LogInformation(
            "Round {Round} of work item {WorkItem} is being asked for again, on attempt {Attempt}.",
            run.RoundNumber,
            run.WorkItemId,
            run.Attempts);

        return true;
    }

    /// <summary>
    /// The round as it is handed to the agent: the same round, with the same brief it had
    /// the first time. The brief is the last thing a reviewer said about the work item,
    /// which is nothing at all on a first round and the reviewer's own words on every
    /// round after one.
    /// </summary>
    private Round RoundFor(Guid workItemId)
    {
        var workItem = _store.Get(workItemId)
            ?? throw new KeyNotFoundException($"no work item {workItemId} to run a round for");

        // The issue's own words travel with the round rather than being left behind as a
        // number the agent would have to go and look up (Round, RoundBrief). Reconstructing
        // the brief from the issue number instead would mean the agent's brief and the
        // board's card could be two different things, and only one of them is the record.
        return new Round(
            workItem.Id,
            workItem.Project,
            workItem.RepoUrl,
            workItem.IssueNumber,
            workItem.IssueTitle,
            workItem.IssueBody,
            workItem.BaseBranch,
            BriefFor(workItemId));
    }

    /// <summary>
    /// Asks the agent for the round, and never lets the call escape: an agent that throws
    /// before it has a task to hand back is a round that came back without a result, and
    /// the machine has to survive that in order to record it. The classification is the
    /// throwing exception's own — see <see cref="Read"/>.
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
            "Round {Round} of work item {WorkItem} ran past the {Timeout} round timeout. "
                + "The spec's own state machine gives a timeout a row of its own: it is a failure, "
                + "it is parked, and it is not retried.",
            run.RoundNumber,
            run.WorkItemId,
            FactoryConstants.RoundTimeout);

        return RoundResult.TimedOut();
    }

    /// <summary>
    /// A round that came back without a result. The classification is the exception's own,
    /// and an exception that did not classify itself is not retried: a retry nobody
    /// classified is the unbounded unattended one that parking exists to prevent, so the
    /// safe answer to "we do not know" is a single attempt and a human.
    /// </summary>
    private RoundResult Read(InFlight run)
    {
        if (run.Pending.IsCompletedSuccessfully)
        {
            return run.Pending.Result;
        }

        var failure = run.Pending.Exception
            ?? throw new InvalidOperationException(
                $"round {run.RoundNumber} of work item {run.WorkItemId} ended with neither a result nor a failure");

        _logger.LogWarning(
            failure,
            "Round {Round} of work item {WorkItem} came back without a result: {Reason}. "
                + "The factory has read that as a {Classification} failure.",
            run.RoundNumber,
            run.WorkItemId,
            failure.Message,
            Failures.Classify(failure));

        return RoundResult.Failed(Failures.Classify(failure));
    }

    /// <summary>
    /// One round this process is inside. The pending call is kept rather than awaited
    /// inline, so that a round is a thing the machine can be asked about instead of a call
    /// it is blocked in — and so that a round waiting out a retry backoff is the same kind
    /// of thing as a round running: an attempt at one round that has not ended.
    /// </summary>
    private sealed class InFlight(Guid workItemId, int roundNumber, DateTimeOffset startedUtc) : IDisposable
    {
        private readonly CancellationTokenSource _rounds = new();

        public Guid WorkItemId { get; } = workItemId;

        public int RoundNumber { get; } = roundNumber;

        public DateTimeOffset StartedUtc { get; } = startedUtc;

        public CancellationToken Token => _rounds.Token;

        /// <summary>How many times this round has been asked for. One until it fails.</summary>
        public int Attempts { get; set; } = 1;

        /// <summary>
        /// When the next attempt may be made, and null while the round is not waiting. Set
        /// only by a failure the round runner called transient, so it is null for a round
        /// that is running, for a permanent failure and for a timeout alike.
        /// </summary>
        public DateTimeOffset? RetryAfterUtc { get; private set; }

        public bool IsWaitingToRetry => RetryAfterUtc is not null;

        public Task<RoundResult> Pending { get; set; } =
            Task.FromResult(RoundResult.Failed(FailureClass.Permanent));

        /// <summary>Ends a round the factory has stopped waiting for.</summary>
        public void End() => _rounds.Cancel();

        /// <summary>
        /// Holds the round open until the wait has passed, and gives the gate back when it
        /// has. Both halves matter: without the first, a transient failure would end the
        /// round, and without the second, the gate would still say "wait" and every step
        /// after the wait would ask for the round again — for ever, and with a round
        /// number nobody is counting.
        /// </summary>
        public void WaitForRetryUntil(DateTimeOffset when) => RetryAfterUtc = when;

        public void TakeTheRetryGate() => RetryAfterUtc = null;

        public void Dispose() => _rounds.Dispose();
    }
}
