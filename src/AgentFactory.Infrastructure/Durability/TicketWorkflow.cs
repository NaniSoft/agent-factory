namespace AgentFactory.Infrastructure.Durability;

using Temporalio.Common;
using Temporalio.Workflows;

/// <summary>The workflow's view of one ticket. Carried once, at start, and never changed.</summary>
public sealed record TicketInput(Guid WorkItemId, bool AutoMerge);

/// <summary>What a decision update answers: whether it applied, and the refusal when it did not.</summary>
public sealed record TicketDecisionResult(bool Applied, string? Refusal);

/// <summary>
/// The per-ticket workflow: the durable half of the factory.
/// </summary>
/// <remarks>
/// <para>
/// Control flow only. Every line that touches a container, the store or GitHub is an
/// activity, and the workflow's decisions are made from activity results and the
/// reviewer's updates — nothing else, because everything else would be a decision replayed
/// differently after a restart. The whole per-ticket policy lives here in the order a
/// reviewer would read it: a ticket is routed; a grilled ticket waits for its answers and
/// hands back; a build ticket waits for acceptance; the rounds run under the reviewer's
/// ceiling, a request for changes is one more round with their words as the brief, a
/// decline is final; and silence past the threshold merges when auto-merge is on.
/// </para>
/// <para>
/// The container budget is not in this file at all: the round worker's activity
/// concurrency is the budget, which makes it global across every ticket by construction
/// rather than by a count workflows would have to agree on with each other.
/// </para>
/// <para>
/// <see cref="TicketInput"/> and <see cref="TicketDecisionResult"/> live beside it rather
/// than with the port's DTOs: they are the workflow's own request and response shapes, not
/// anything an activity's signature names, so they belong with the driver that carries them
/// (<see cref="TicketDecision"/>, which an activity *does* name, is in the same namespace
/// as the interface that names it, because the workflow and its activities are one layer).
/// </para>
/// </remarks>
[Workflow]
public sealed class TicketWorkflow
{
    private static readonly ActivityOptions RoundOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(100),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(10),
            BackoffCoefficient = 2,
            MaximumInterval = TimeSpan.FromMinutes(5),
            MaximumAttempts = 3,
            NonRetryableErrorTypes = ["AgentFactory.Domain.Failures.PermanentFailure"],
        },
    };

    private static readonly ActivityOptions Steps = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(5),
        RetryPolicy = new RetryPolicy { MaximumAttempts = 3 },
    };

    private bool _accepted;
    private bool _inReview;
    private TicketDecision? _pendingDecision;
    private TicketDecisionResult? _answer;
    private long _decisionTaken;
    private long _decisionAnswered;

    [WorkflowRun]
    public async Task RunAsync(TicketInput input) =>
        await RunFromAsync(input, feedback: null, roundNumber: 1);

    private async Task RunFromAsync(TicketInput input, string? feedback, int roundNumber)
    {
        // Route. A grilled or charted ticket waits for its human, hands back when the
        // answers arrive in the conversation, and routes again — as many times as the
        // answers ask for. A ticket whose route parked ends here: it is in Escalated, and
        // a human finishes it from the board.
        var route = await RouteAsync(input, roundNumber);
        while (route.Kind is "Grill" or "Wayfinder")
        {
            while (!await Workflow.ExecuteActivityAsync(
                (ITicketActivities a) => a.AnswerArrived(input.WorkItemId), Steps))
            {
                // Durable waiting. In production this is one small activity call every
                // five seconds per waiting ticket; under the test environment it is
                // instant, because the test clock skips.
                await Workflow.DelayAsync(TimeSpan.FromSeconds(5));
            }

            await Workflow.ExecuteActivityAsync((ITicketActivities a) => a.HandBack(input.WorkItemId), Steps);
            route = await RouteAsync(input, ++roundNumber);
        }

        if (route.ParkedBecause is not null)
        {
            return;
        }

        // A build ticket waits for the reviewer's click. Nothing is spent before it, and
        // the wait is durable: the click may come tomorrow, or after ten restarts.
        await Workflow.WaitConditionAsync(() => _accepted);

        // The rounds, under the reviewer's ceiling.
        while (true)
        {
            await Workflow.ExecuteActivityAsync((ITicketActivities a) => a.BeginBuild(input.WorkItemId), Steps);
            var build = await Workflow.ExecuteActivityAsync(
                (ITicketActivities a) => a.BuildRoundAsync(input.WorkItemId, roundNumber, feedback), RoundOptions);

            if (!build.Produced)
            {
                // Landed in Escalated by the activity: a round that did not run to
                // completion is parked for a human, and no fourth attempt is reachable.
                return;
            }

            _inReview = true;
            var decision = await ReviewAsync(input);
            _inReview = false;

            if (decision.Decision == "approve")
            {
                await Workflow.ExecuteActivityAsync((ITicketActivities a) => a.MergeAsync(input.WorkItemId), RoundOptions);
                Answer(new TicketDecisionResult(true, null));
                return;
            }

            if (decision.Decision == "reject")
            {
                await Workflow.ExecuteActivityAsync((ITicketActivities a) => a.Reject(input.WorkItemId), Steps);
                Answer(new TicketDecisionResult(true, null));
                return;
            }

            // A request for changes is one more round with the reviewer's words as its
            // brief — up to the ceiling. A ceiling spent is a ticket parked for a human.
            roundNumber++;
            if (roundNumber > 3)
            {
                await Workflow.ExecuteActivityAsync((ITicketActivities a) => a.Escalate(input.WorkItemId), Steps);
                Answer(new TicketDecisionResult(true, null));
                return;
            }

            feedback = decision.Feedback;
            Answer(new TicketDecisionResult(true, null));
        }
    }

    /// <summary>Answers the board's decision update: the decision has been applied to the record.</summary>
    private void Answer(TicketDecisionResult result)
    {
        _answer = result;
        _decisionAnswered++;
    }

    private async Task<RouteLanded> RouteAsync(TicketInput input, int roundNumber) =>
        await Workflow.ExecuteActivityAsync(
            (ITicketActivities a) => a.RouteRoundAsync(input.WorkItemId, roundNumber), RoundOptions);

    /// <summary>
    /// The review: the reviewer's decision, or silence past the threshold. The decision
    /// arrives as an update from the board, and the update's answer comes back once the
    /// workflow has applied it — so the board's click still says applied or refused in its
    /// own words, exactly as the old endpoint did.
    /// </summary>
    private async Task<TicketDecision> ReviewAsync(TicketInput input)
    {
        var deadline = Workflow.UtcNow + TimeSpan.FromHours(48);

        await Workflow.WaitConditionAsync(
            () => _pendingDecision is not null
                || (input.AutoMerge && Workflow.UtcNow >= deadline));

        if (_pendingDecision is null)
        {
            // Silence past the threshold is the factory's own decision, and it is a merge.
            var silence = new TicketDecision(
                "approve",
                "Merged by silence: no reviewer decided within 48 hours and auto-merge is on.");
            await Workflow.ExecuteActivityAsync(
                (ITicketActivities a) => a.RecordDecision(input.WorkItemId, silence), Steps);
            return silence;
        }

        var decision = _pendingDecision;
        _pendingDecision = null;

        await Workflow.ExecuteActivityAsync(
            (ITicketActivities a) => a.RecordDecision(input.WorkItemId, decision), Steps);

        return decision;
    }

    /// <summary>The acceptance gate: the reviewer's click that lets a build begin.</summary>
    [WorkflowUpdate]
    public Task Accept()
    {
        _accepted = true;
        return Task.CompletedTask;
    }

    /// <summary>The reviewer's decision on a change in Review, answered once it has applied.</summary>
    [WorkflowUpdate]
    public async Task<TicketDecisionResult> DecideAsync(TicketDecision decision)
    {
        if (!_inReview)
        {
            return new TicketDecisionResult(
                false,
                "That ticket is not in Review, so there is nothing to decide. A ticket in Backlog is accepted, "
                    + "and a ticket in Waiting is waiting on its answers.");
        }

        if (decision.Decision is not ("approve" or "request-changes" or "reject"))
        {
            return new TicketDecisionResult(
                false,
                "That is not one of the three decisions. A work item in Review is approved, sent back for "
                    + "changes, or rejected, and there is nothing else to decide.");
        }

        if (decision.Decision == "request-changes" && string.IsNullOrWhiteSpace(decision.Feedback))
        {
            return new TicketDecisionResult(
                false,
                "A request for changes carries the reviewer's own words, because they are the next round's "
                    + "brief. Say what has to change.");
        }

        // The decision is taken here and answered by the flow once it has applied — after
        // the merge for an approve, after the move for a reject, after the brief is set for
        // a request for changes. The ticket's own generation counters keep two decisions
        // from answering each other.
        var taken = ++_decisionTaken;
        _pendingDecision = decision;

        await Workflow.WaitConditionAsync(() => _decisionAnswered >= taken);

        var answer = _answer!;
        _answer = null;
        return answer;
    }
}
