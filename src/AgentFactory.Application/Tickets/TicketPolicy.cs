namespace AgentFactory.Application.Tickets;

using AgentFactory.Application.GitHub;
using AgentFactory.Application.Rounds;
using AgentFactory.Application.Routing;
using AgentFactory.Application.WorkItems;
using AgentFactory.Domain.Failures;
using AgentFactory.Domain.Issues;
using AgentFactory.Domain.Rounds;
using AgentFactory.Domain.Routing;
using AgentFactory.Domain.WorkItems;

/// <summary>
/// The factory's transition rules, in one place, for the two drivers that step them
/// (#64). The in-process loop calls this between its own steps; the durable workflow
/// calls it from its activities. What the two drivers keep for themselves is only *how*
/// they are stepped and *how* they are made durable — a step gate, an in-flight budget,
/// a clock for a timeout here, a workflow history and an engine retry policy there — so
/// a rule that exists twice is a rule that can drift into two answers.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is a component and not a port.</strong> It is not behind an interface
/// because there is no boundary behind it: it holds the same seams the loop already
/// held, and what it adds is composition rather than a new way to reach the outside
/// world. The map's rule is "one policy core, two drivers", and this is the core.
/// </para>
/// <para>
/// <strong>What is deliberately not here is the part the two drivers genuinely disagree
/// about today.</strong> The loop's round timeout and retry, its merge-retry ledger and
/// its feedback threshold are its own; the workflow's durable waits and its engine retry
/// policy are its own. So are the two places the drivers disagree about a *rule* —
/// whether applying a decision marks it applied, and whether a routing round may spend a
/// reviewer's round. Those are reported beside this file rather than quietly resolved
/// here, because changing one would be a policy change rather than a move of code.
/// </para>
/// <para>
/// It takes no logger, and that is the same judgement: the components that phrase a
/// record are the drivers, and a rule that carried its own wording would make two
/// drivers that phrase one transition differently stop being able to.
/// </para>
/// </remarks>
public sealed class TicketPolicy(
    IWorkItemStore store,
    INOpenCode agent,
    IGitHub github,
    IMergePolicy merger,
    IIssuePublisher publisher)
{
    /// <summary>
    /// Asks the agent for the round, and never lets the call escape. One call is one
    /// round, and this is the one place in the process that makes it: a second caller
    /// would be a second way to start a round that nothing counting containers could see.
    /// </summary>
    /// <remarks>
    /// An agent that throws before it has a task to hand back becomes a round that came
    /// back without a result rather than a fault in the middle of a step; the
    /// classification is the throwing exception's own. This is deliberate for both
    /// drivers: a workflow activity that awaited a synchronously-thrown exception would
    /// see the same exception either way, and a loop that held the task has to be able
    /// to read the failure off it later.
    /// </remarks>
    public Task<RoundResult> RunRoundAsync(Round round, CancellationToken cancellationToken)
    {
        try
        {
            return agent.RunRoundAsync(round, cancellationToken);
        }
        catch (Exception failure)
        {
            return Task.FromException<RoundResult>(failure);
        }
    }

    /// <summary>
    /// The conversation the round travels with: the issue's own comments and, when there
    /// is one, its questions issue's — in the order they were said. An issue is a
    /// conversation and not a document, and the answers a Grill waits on live in the
    /// conversation rather than in the body.
    /// </summary>
    /// <remarks>
    /// It throws rather than containing a read that fails, so each driver phrases what it
    /// did not get in its own words: the loop says the round is asked with the issue alone
    /// and the next one reads again, and the workflow says the same in its own record.
    /// Neither is allowed to answer a question with no answers in hand and call it read.
    /// </remarks>
    public async Task<IReadOnlyList<IssueComment>> ConversationAsync(
        WorkItem workItem,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        var conversation = new List<IssueComment>();
        conversation.AddRange(await github.ReadCommentsAsync(
            workItem.RepoUrl, workItem.IssueNumber, cancellationToken).ConfigureAwait(false));

        if (workItem.RoutingIssue is { } question && question != workItem.IssueNumber)
        {
            conversation.AddRange(await github.ReadCommentsAsync(
                workItem.RepoUrl, question, cancellationToken).ConfigureAwait(false));
        }

        // A stable order, because the loop's was: two comments carrying the same timestamp
        // must not be handed to the agent in a different order than the loop would have.
        return conversation.OrderBy(comment => comment.CreatedUtc).ToList();
    }

    /// <summary>
    /// The round as it is handed to the agent: the work item's own words, the reviewer's
    /// most recent brief, its number and attempt, and the conversation it travels with.
    /// </summary>
    /// <remarks>
    /// The number and the attempt are passed in rather than read off the work item, because
    /// the two drivers count them differently and each is the only thing that knows which
    /// round and which attempt it is asking for. What they must not differ about is the
    /// shape of the round — the fields, the brief and the conversation — and that is what
    /// this is one place for.
    /// </remarks>
    public static Round ComposeRound(
        WorkItem workItem,
        RoundPurpose purpose,
        int roundNumber,
        int attempt,
        string feedback,
        IReadOnlyList<IssueComment> conversation)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        return new Round(
            workItem.Id,
            workItem.Project,
            workItem.RepoUrl,
            workItem.IssueNumber,
            workItem.IssueTitle,
            workItem.IssueBody,
            workItem.BaseBranch,
            feedback,
            roundNumber,
            attempt,
            purpose,
            conversation);
    }

    /// <summary>
    /// Lands a build round: the result is recorded against the work item and the work item
    /// moves to where the outcome means. A round that came back with a result is what a
    /// reviewer judges, so it goes to Review; a round that did not is not merged over and
    /// parks in Escalated, where a human can still merge or decline it (ADR-0008).
    /// </summary>
    /// <remarks>
    /// The two drivers pass the timestamp and the attempt count they hold: the loop knows
    /// when it asked and how many times, and the workflow is stepped by the engine and
    /// records the attempt the activity itself is. Neither is a rule; both are facts the
    /// caller has and this method does not.
    /// </remarks>
    public RoundResultRecord LandTheBuild(
        Guid workItemId,
        RoundResult result,
        DateTimeOffset startedUtc,
        int attempts)
    {
        ArgumentNullException.ThrowIfNull(result);

        // Kept beside the payload and passed straight through. What the round produced is
        // what the round produced, and nothing here adds to it or takes away from it.
        var round = store.RecordRound(
            workItemId,
            result.Outcome,
            result.ResultPayload,
            result.AgentNote,
            startedUtc,
            attempts,
            result.Failure,
            result.Log,
            result.Diff);

        store.Move(
            workItemId,
            result.Outcome == RoundOutcome.Produced ? Swimlane.Review : Swimlane.Escalated);

        return round;
    }

    /// <summary>
    /// Lands a routing round: records what it decided, and files whatever it decided a
    /// human has to be given. A round that could not answer parks the work item with the
    /// driver's own words for why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The questions are filed before the verdict is recorded, and that order is
    /// the point.</strong> A verdict of <c>Grill</c> recorded without the question issue
    /// existing would leave a work item in Waiting with nothing to answer and nothing
    /// pointing at where to answer it, so publication comes first and a publication that
    /// fails turns the verdict into a park carrying the refusal.
    /// </para>
    /// <para>
    /// <paramref name="unfinishedBecause"/> is the driver's, and it is the one thing the
    /// two drivers phrase differently today: the loop tells a timeout, a transient failure
    /// and any other unfinished round apart, and the workflow distinguishes only the
    /// timeout. Passing the words in keeps each driver's card saying what it says now while
    /// the rule — park, do not guess a verdict — is one rule.
    /// </para>
    /// <para>
    /// A verdict nobody can read parks as well: a default that built things would be the
    /// one default that spends a container on work nobody decided should be spent.
    /// </para>
    /// </remarks>
    public async Task<RouteLanding> LandTheRouteAsync(
        WorkItem workItem,
        RoundResult result,
        string unfinishedBecause,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        ArgumentNullException.ThrowIfNull(result);

        if (result.Outcome != RoundOutcome.Produced)
        {
            Park(workItem.Id, unfinishedBecause);
            return RouteLanding.Parked(unfinishedBecause);
        }

        if (result.Routing is not { } outcome)
        {
            const string because =
                "the routing round came back with no verdict at all, so nothing has said what kind of work this is";
            Park(workItem.Id, because);
            return RouteLanding.Parked(because);
        }

        if (outcome is not { IsReadable: true, Proposal: { } proposal })
        {
            var because = outcome.UnreadableBecause ?? "the routing round's verdict could not be read";
            Park(workItem.Id, because);
            return RouteLanding.Parked(because);
        }

        int? filedAs;
        try
        {
            filedAs = await publisher.PublishAsync(workItem, proposal, cancellationToken).ConfigureAwait(false);
        }
        catch (FactoryFailure refused)
        {
            // The questions were not filed, so the verdict cannot be acted on and is not
            // recorded. The reason names the refusal, because a project whose credential
            // can push but cannot write issues will hit this on every routing round.
            var because = $"the factory could not file what {proposal.Verdict} asks for: {refused.Message}";
            Park(workItem.Id, because);
            return RouteLanding.Parked(because);
        }

        store.RecordRoute(workItem.Id, proposal.Verdict, proposal.Because, filedAs);
        return new RouteLanding(proposal.Verdict, filedAs, proposal.Because, ParkedBecause: null);
    }

    /// <summary>
    /// Parks a work item whose routing round could not answer, keeping the round's own
    /// account of why. Deliberately not through the merge-failure path and not through a
    /// decision: neither of those is what happened, and borrowing either would put a
    /// counter on the work item that a later merge would then inherit.
    /// </summary>
    public void Park(Guid workItemId, string because) =>
        store.RecordRoute(workItemId, WorkItemKind.Unrouted, because);

    /// <summary>
    /// Hands a grilled work item back to the pipeline: Backlog, unrouted, with its
    /// questions issue kept, because that issue's conversation is where the answers live.
    /// </summary>
    public void HandBack(Guid workItemId) => store.RecordHandBack(workItemId);

    /// <summary>
    /// Whether the answers have arrived on a waiting work item's questions issue: any
    /// comment newer than the work item's own last write is an answer, and a work item in
    /// Waiting is written by nothing else, so the timestamp is the whole test.
    /// </summary>
    /// <remarks>
    /// It throws rather than containing a read that fails, for the reason
    /// <see cref="ConversationAsync"/> does: a conversation that is temporarily unreachable
    /// is not a reason to park a work item who waits on, and each driver logs it in its own
    /// words and reads again next time.
    /// </remarks>
    public async Task<bool> AnswerArrivedAsync(WorkItem workItem, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        if (workItem.RoutingIssue is not { } question)
        {
            return false;
        }

        var conversation = await github.ReadCommentsAsync(workItem.RepoUrl, question, cancellationToken)
            .ConfigureAwait(false);

        return conversation.Any(comment => comment.CreatedUtc > workItem.UpdatedUtc);
    }

    /// <summary>
    /// Ships the work item's change through the merge seam. The loop learns whether it
    /// landed; what a pull request is, and how the branch is pushed, stays behind the seam.
    /// </summary>
    public Task MergeAsync(WorkItem workItem, CancellationToken cancellationToken) =>
        merger.MergeAsync(workItem, cancellationToken);
}

/// <summary>
/// What became of a routing round when it was landed. A verdict and the issue it filed,
/// both null-safe, or the driver's own words for why the work item parked — never both.
/// </summary>
/// <remarks>
/// It parallels the durable workflow's own <c>RouteLanded</c> rather than replacing it:
/// that one is a workflow input and this one is policy's output, and keeping the
/// translation in the activity is what stops a serializer from deciding the shape of a
/// rule.
/// </remarks>
/// <param name="Kind">The verdict, or <see cref="WorkItemKind.Unrouted"/> when the round could not answer.</param>
/// <param name="QuestionsIssue">The issue the factory filed, or null when it filed nothing.</param>
/// <param name="Reason">The round's own reason for the verdict, or the park's reason. Required.</param>
/// <param name="ParkedBecause">Why the work item parked, or null when it did not.</param>
public sealed record RouteLanding(
    WorkItemKind Kind,
    int? QuestionsIssue,
    string Reason,
    string? ParkedBecause)
{
    /// <summary>A routing round that could not answer, with the driver's own words for why.</summary>
    public static RouteLanding Parked(string because) =>
        new(WorkItemKind.Unrouted, QuestionsIssue: null, because, ParkedBecause: because);
}
