namespace AgentFactory.Application.Durability;

using AgentFactory.Domain.Failures;
using Temporalio.Activities;

/// <summary>
/// The world the per-ticket workflow may touch, as activities. Everything here is the
/// factory's own existing machinery — the round runner, the publisher, the merger, the
/// store — with no new behaviour: the workflow decides the order, the activities do the
/// work, and a restart of this process replays the order without redoing the work.
/// </summary>
/// <remarks>
/// <para>
/// It is an <strong>inbound</strong> port, and the only one in this project: the durable
/// workflow lives outside and drives the factory through it, so the caller is outside and
/// the contract is the inside's — the same shape as an HTTP endpoint. The implementations
/// (<c>TicketActivities</c>, <c>TicketWorkflow</c>, <c>TemporalTickets</c>,
/// <c>NullTicketStarter</c>) are in <c>Infrastructure</c>, beside the engine they adapt.
/// </para>
/// <para>
/// <strong>This port is why `Application` names `Temporalio`, and that is a correction to
/// the dependency rule rather than an oversight.</strong> The engine resolves every
/// activity call from an <see cref="ActivityAttribute"/> on the very method the workflow
/// invokes — <c>ActivityDefinition.NameFromMethodForCall</c> throws without one — so an
/// inbound durable port cannot be declared without the engine's own vocabulary. The two
/// alternatives were both worse: an attribute-free port does not work at all, and moving
/// the attributes to <c>TicketActivities</c> would make the workflow call the concrete
/// adapter and leave the port decorative. Recorded in <c>docs/architecture.md</c>.
/// </para>
/// <para>
/// The retry policy these activities carry is the failure classification the components
/// already declare: a <see cref="PermanentFailure"/> is never retried, and everything else
/// comes back three times with a growing wait — the same shape the loop's own retry policy
/// had, now carried by the engine so it survives restarts with the rest of the workflow.
/// </para>
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
/// One reviewer decision, as the board's own form carries it. <see cref="Feedback"/> is
/// nullable because a reviewer's keyboard is not a schema: an approval and a rejection
/// both carry no words, and only a request for changes insists on them. It travels as a
/// workflow input, which is another reason to say so here rather than let a null cross
/// a serializer as if it were a string.
/// </summary>
public sealed record TicketDecision(string Decision, string? Feedback);
