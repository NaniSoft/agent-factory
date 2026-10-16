namespace AgentFactory.Infrastructure.Durability;

using AgentFactory.Application.Durability;
using AgentFactory.Application.Tickets;
using AgentFactory.Application.WorkItems;
using AgentFactory.Domain.Failures;
using AgentFactory.Domain.Issues;
using AgentFactory.Domain.Rounds;
using AgentFactory.Domain.WorkItems;
using Microsoft.Extensions.Logging;

/// <summary>
/// The activities' implementation: the factory's own transition rules, composed per
/// ticket. The work item id travels as an argument to every activity, because a workflow's
/// history is replayed and an ambient context would be a guess at where the replay was.
/// </summary>
/// <remarks>
/// <para>
/// It is a thin adapter over <see cref="TicketPolicy"/> (#64), the same policy core the
/// in-process loop calls: the workflow decides the order, the activities do the work, and
/// the rules about what a landing, a park or a merge *means* exist once. What is left here
/// is the workflow's own half — the durable waits, the reviewer's updates and the store
/// operations the loop applies through a decision, which it does differently (see the
/// divergences noted beside <see cref="TicketPolicy"/>).
/// </para>
/// <para>
/// A store operation that is a *decision* rather than a transition stays here: the
/// workflow records a decision and moves the work item, where the loop applies the decision
/// so the record says it was applied. That difference is reported rather than resolved,
/// because resolving it would change what one of the two drivers does.
/// </para>
/// </remarks>
public sealed class TicketActivities(
    IWorkItemStore store,
    TicketPolicy policy,
    ILogger<TicketActivities> logger) : ITicketActivities
{
    public async Task<RouteLanded> RouteRoundAsync(Guid workItemId, int roundNumber)
    {
        var round = await ComposeAsync(workItemId, RoundPurpose.Route, roundNumber, feedback: null);
        var result = await policy.RunRoundAsync(round, CancellationToken.None);

        var workItem = store.Get(workItemId)
            ?? throw new KeyNotFoundException($"no work item {workItemId}");

        var landing = await policy.LandTheRouteAsync(
            workItem,
            result,
            UnfinishedBecause(result),
            CancellationToken.None);

        if (landing.ParkedBecause is { } because)
        {
            return new RouteLanded(landing.Kind.ToString(), landing.QuestionsIssue, landing.Reason, because);
        }

        logger.LogInformation(
            "{Project}#{Issue} was routed as {Kind}: {Reason}",
            workItem.Project,
            workItem.IssueNumber,
            landing.Kind,
            landing.Reason);

        return new RouteLanded(landing.Kind.ToString(), landing.QuestionsIssue, landing.Reason, null);
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
            return policy.AnswerArrivedAsync(workItem, CancellationToken.None)
                .ConfigureAwait(false).GetAwaiter().GetResult();
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

    public void HandBack(Guid workItemId) => policy.HandBack(workItemId);

    public void BeginBuild(Guid workItemId) => store.Move(workItemId, Swimlane.Frontier);

    public async Task<BuildLanded> BuildRoundAsync(Guid workItemId, int roundNumber, string? feedback)
    {
        var round = await ComposeAsync(workItemId, RoundPurpose.Build, roundNumber, feedback);
        store.Move(workItemId, Swimlane.InProgress);
        var result = await policy.RunRoundAsync(round, CancellationToken.None);

        policy.LandTheBuild(workItemId, result, DateTimeOffset.UtcNow, attempts: 1);

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
            await policy.MergeAsync(item, CancellationToken.None);
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
        // The read and the ordering are `TicketPolicy`'s; what is here is the workflow's own
        // words for a read that failed, because a round with less to go on is not a reason
        // to spend a container finding that out again.
        IReadOnlyList<IssueComment> conversation;
        try
        {
            conversation = await policy.ConversationAsync(workItem, CancellationToken.None);
        }
        catch (Exception refused)
        {
            logger.LogWarning(
                "The conversation on {Repository}#{Issue} could not be read: {Refusal}. The round is asked "
                    + "with the issue alone.",
                workItem.RepoUrl,
                workItem.IssueNumber,
                refused.Message);
            conversation = [];
        }

        return TicketPolicy.ComposeRound(
            workItem,
            purpose,
            roundNumber,
            attempt: 1,
            feedback ?? string.Empty,
            conversation);
    }

    /// <summary>
    /// The workflow's own words for a routing round that could not answer. The policy core
    /// carries whatever the driver says; the loop tells a timeout, a transient failure and
    /// any other unfinished round apart, and this does not. That difference is reported
    /// beside <see cref="TicketPolicy"/> rather than resolved here.
    /// </summary>
    private static string UnfinishedBecause(RoundResult result) =>
        result.Outcome == RoundOutcome.TimedOut
            ? "the routing round ran past its timeout and was ended; nothing has said what kind of work this is"
            : "a routing round failed transiently without saying what kind of work this is";
}
