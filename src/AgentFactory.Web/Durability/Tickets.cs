namespace AgentFactory.Durability;

using AgentFactory.Application.Credentials;
using AgentFactory.Domain.Failures;
using AgentFactory.Domain.Rounds;
using AgentFactory.GitHub;
using AgentFactory.Routing;
using AgentFactory.WorkItems;
using AgentFactory.Rounds;
using Microsoft.Extensions.Logging;
using Temporalio.Activities;
using Temporalio.Common;
using Temporalio.Workflows;

/// <summary>
/// The world the per-ticket workflow may touch, as activities. Everything here is the
/// factory's own existing machinery — the round runner, the publisher, the merger, the
/// store — with no new behaviour: the workflow decides the order, the activities do the
/// work, and a restart of this process replays the order without redoing the work.
/// </summary>
/// <remarks>
/// The retry policy these activities carry is the failure classification the components
/// already declare: a <see cref="PermanentFailure"/> is never retried, and everything else
/// comes back three times with a growing wait — the same shape the loop's own retry policy
/// had, now carried by the engine so it survives restarts with the rest of the workflow.
/// </remarks>
public interface ITicketActivities
{
    [Activity("route-round")]
    Task<RouteLanded> RouteRoundAsync(Guid workItemId, int roundNumber);

    [Activity("answer-arrived")]
    bool AnswerArrived(Guid workItemId);

    [Activity("hand-back")]
    void HandBack(Guid workItemId);

    [Activity("begin-build")]
    void BeginBuild(Guid workItemId);

    [Activity("build-round")]
    Task<BuildLanded> BuildRoundAsync(Guid workItemId, int roundNumber, string? feedback);

    [Activity("merge")]
    Task<MergeLanded> MergeAsync(Guid workItemId);

    [Activity("reject")]
    void Reject(Guid workItemId);

    [Activity("escalate")]
    void Escalate(Guid workItemId);

    [Activity("record-decision")]
    void RecordDecision(Guid workItemId, TicketDecision decision);
}

public record RouteLanded(string Kind, int? QuestionsIssue, string Reason, string? ParkedBecause);

public record BuildLanded(bool Produced, string Outcome);

public record MergeLanded(bool Merged, string? ParkedBecause);

/// <summary>
/// The activities' implementation: the factory's own components, composed per ticket. The
/// work item id travels as an argument to every activity, because a workflow's history is
/// replayed and an ambient context would be a guess at where the replay was.
/// </summary>
public sealed class TicketActivities(
    IWorkItemStore store,
    INOpenCode agent,
    IssuePublisher publisher,
    IGitHub github,
    ILogger<TicketActivities> logger) : ITicketActivities
{
    public async Task<RouteLanded> RouteRoundAsync(Guid workItemId, int roundNumber)
    {
        var round = await ComposeAsync(workItemId, RoundPurpose.Route, roundNumber, feedback: null);
        var result = await agent.RunRoundAsync(round, CancellationToken.None);

        var workItem = store.Get(workItemId)
            ?? throw new KeyNotFoundException($"no work item {workItemId}");

        if (result.Outcome != RoundOutcome.Produced)
        {
            var because = result.Outcome == RoundOutcome.TimedOut
                ? "the routing round ran past its timeout and was ended; nothing has said what kind of work this is"
                : "a routing round failed transiently without saying what kind of work this is";
            Park(workItem, because);
            return new RouteLanded("Unrouted", null, because, because);
        }

        if (result.Routing is not { } outcome)
        {
            const string because = "the routing round came back with no verdict at all, so nothing has said what kind of work this is";
            Park(workItem, because);
            return new RouteLanded("Unrouted", null, because, because);
        }

        if (outcome is not { IsReadable: true, Proposal: { } proposal })
        {
            var unreadable = outcome.UnreadableBecause ?? "the routing round's verdict could not be read";
            Park(workItem, unreadable);
            return new RouteLanded("Unrouted", null, unreadable, unreadable);
        }

        int? filedAs;
        try
        {
            filedAs = await publisher.PublishAsync(workItem, proposal, CancellationToken.None);
        }
        catch (FactoryFailure refused)
        {
            var because = $"the factory could not file what {proposal.Verdict} asks for: {refused.Message}";
            Park(workItem, because);
            return new RouteLanded("Unrouted", null, because, because);
        }

        store.RecordRoute(workItem.Id, proposal.Verdict, proposal.Because, filedAs);

        logger.LogInformation(
            "{Project}#{Issue} was routed as {Kind}: {Reason}",
            workItem.Project,
            workItem.IssueNumber,
            proposal.Verdict,
            proposal.Because);

        return new RouteLanded(proposal.Verdict.ToString(), filedAs, proposal.Because, null);
    }

    public bool AnswerArrived(Guid workItemId)
    {
        var item = store.Get(workItemId);
        if (item is not { } workItem || workItem.RoutingIssue is not { } question)
        {
            return false;
        }

        try
        {
            var conversation = github.ReadCommentsAsync(workItem.RepoUrl, question, CancellationToken.None)
                .ConfigureAwait(false).GetAwaiter().GetResult();
            return conversation.Any(comment => comment.CreatedUtc > workItem.UpdatedUtc);
        }
        catch (Exception refused)
        {
            logger.LogWarning(
                "The conversation on {Repository}#{Question} could not be read: {Refusal}. The ticket keeps waiting.",
                workItem.RepoUrl,
                question,
                refused.Message);
            return false;
        }
    }

    public void HandBack(Guid workItemId) => store.RecordHandBack(workItemId);

    public void BeginBuild(Guid workItemId) => store.Move(workItemId, Swimlane.Frontier);

    public async Task<BuildLanded> BuildRoundAsync(Guid workItemId, int roundNumber, string? feedback)
    {
        var round = await ComposeAsync(workItemId, RoundPurpose.Build, roundNumber, feedback);
        store.Move(workItemId, Swimlane.InProgress);
        var result = await agent.RunRoundAsync(round, CancellationToken.None);

        store.RecordRound(
            workItemId,
            result.Outcome,
            result.ResultPayload,
            result.AgentNote,
            DateTimeOffset.UtcNow,
            attempts: 1,
            failure: result.Failure,
            log: result.Log,
            diff: result.Diff);

        store.Move(
            workItemId,
            result.Outcome == RoundOutcome.Produced ? Swimlane.Review : Swimlane.Escalated);

        logger.LogInformation(
            "Round {Round} of the ticket returned {Outcome}.",
            roundNumber,
            result.Outcome);

        return new BuildLanded(result.Outcome == RoundOutcome.Produced, result.Outcome.ToString());
    }

    public async Task<MergeLanded> MergeAsync(Guid workItemId)
    {
        var item = store.Get(workItemId) ?? throw new KeyNotFoundException($"no work item {workItemId}");

        try
        {
            await github.MergeAsync(item.RepoUrl, item.IssueNumber, CancellationToken.None);
        }
        catch (PermanentFailure refused)
        {
            // A merge that is refused for good — a declined pull request, a branch that
            // moved on — parks exactly as the loop parked it: for a human, mergeable from
            // the board, and never tried again by the factory on its own.
            store.Move(workItemId, Swimlane.Escalated);
            return new MergeLanded(false, refused.Message);
        }

        store.Move(workItemId, Swimlane.Done);
        return new MergeLanded(true, null);
    }

    public void Reject(Guid workItemId) => store.Move(workItemId, Swimlane.Rejected);

    public void Escalate(Guid workItemId) => store.Move(workItemId, Swimlane.Escalated);

    public void RecordDecision(Guid workItemId, TicketDecision decision)
    {
        if (!Decisions.TryParse(decision.Decision, out var made))
        {
            throw new InvalidOperationException(
                $"'{decision.Decision}' is not one of the three decisions a reviewer can make");
        }

        store.RecordDecision(workItemId, made, decision.Feedback);
    }

    private async Task<Round> ComposeAsync(Guid workItemId, RoundPurpose purpose, int roundNumber, string? feedback)
    {
        var workItem = store.Get(workItemId)
            ?? throw new KeyNotFoundException($"no work item {workItemId} to run a round for");

        // The conversation travels with the round: the issue's comments, and — when one
        // exists — its questions issue's comments, in the order they were said. An answer
        // a human left is the thing this round may be riding on, so it is quoted whole.
        var conversation = new List<IssueComment>();
        try
        {
            conversation.AddRange(await github.ReadCommentsAsync(
                workItem.RepoUrl, workItem.IssueNumber, CancellationToken.None));
            if (workItem.RoutingIssue is { } question && question != workItem.IssueNumber)
            {
                conversation.AddRange(await github.ReadCommentsAsync(
                    workItem.RepoUrl, question, CancellationToken.None));
            }
        }
        catch (Exception refused)
        {
            logger.LogWarning(
                "The conversation on {Repository}#{Issue} could not be read: {Refusal}. The round is asked "
                    + "with the issue alone.",
                workItem.RepoUrl,
                workItem.IssueNumber,
                refused.Message);
        }

        conversation.Sort((left, right) => left.CreatedUtc.CompareTo(right.CreatedUtc));

        return new Round(
            workItem.Id,
            workItem.Project,
            workItem.RepoUrl,
            workItem.IssueNumber,
            workItem.IssueTitle,
            workItem.IssueBody,
            workItem.BaseBranch,
            feedback ?? string.Empty,
            roundNumber,
            1,
            purpose,
            conversation);
    }

    private void Park(WorkItem workItem, string because)
    {
        store.RecordRoute(workItem.Id, WorkItemKind.Unrouted, because);
        store.Move(workItem.Id, Swimlane.Escalated);
    }
}

/// <summary>The workflow's view of one ticket. Carried once, at start, and never changed.</summary>
public sealed record TicketInput(Guid WorkItemId, bool AutoMerge);

/// <summary>
/// One reviewer decision, as the board's own form carries it. <see cref="Feedback"/> is
/// nullable because a reviewer's keyboard is not a schema: an approval and a rejection
/// both carry no words, and only a request for changes insists on them. It travels as a
/// workflow input, which is another reason to say so here rather than let a null cross
/// a serializer as if it were a string.
/// </summary>
public sealed record TicketDecision(string Decision, string? Feedback);

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
