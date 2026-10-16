namespace AgentFactory.Infrastructure.Durability;

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
/// <strong>This is not a port, and moving it here is the correction.</strong> It was
/// declared in <c>AgentFactory.Application.Durability</c> as an "inbound port", and it was
/// never one. A port exists to <em>invert</em> a dependency: an inner component needs
/// something done and must not name the outer thing that does it, so the contract is
/// declared where the need is. Here there is nothing to invert — the workflow and its
/// activities are the same layer, both <c>Infrastructure</c>, and the workflow calls the
/// activities by name because they are the same driver's two halves. What
/// <c>Application</c> offers the durable path is its contract with the driver, and that is
/// already <c>TicketPolicy</c>, which both drivers call.
/// </para>
/// <para>
/// Declaring it in <c>Application</c> bought nothing and cost <c>Application</c> a
/// reference to a workflow engine, which is the one property the dependency rule exists to
/// protect. The engine resolves every activity call from an <see cref="ActivityAttribute"/>
/// on the very method the workflow invokes — <c>ActivityDefinition.NameFromMethodForCall</c>
/// throws without one — so an activity interface cannot be declared without the engine's
/// own vocabulary, and the choice is between naming the engine where it is used and pulling
/// it into the policy core. This interface is the driver's <strong>internal seam</strong>:
/// useful for testing, and not a boundary.
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
