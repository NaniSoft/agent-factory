namespace AgentFactory.Application.Routing;

using AgentFactory.Domain.Routing;
using AgentFactory.Domain.WorkItems;

/// <summary>
/// Where a routing round's questions go so that a human can answer them, and what the loop
/// is told it became.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is a port, and it is the one the dependency rule could not have been told
/// about.</strong> The loop reached the concrete publisher directly, which is the rule's one
/// real violation: `Application` naming a type that lives on the far side of the boundary.
/// The inside says what it needs - a verdict turned into the issue a person is asked on -
/// and the outside says how it is done, which is GitHub's API on the host because a round
/// holds no write credential (ADR-0006, docs/architecture.md rule 1).
/// </para>
/// <para>
/// It is narrow on purpose, and the narrowness is the point rather than a limitation. Which
/// issues a map becomes, in what order, and how a blocking edge is wired are the shape of a
/// proposal somebody else wrote, not the loop's policy. What <em>is</em> policy, and stays in
/// the loop, is that a verdict is recorded only once the questions a human has to answer
/// actually exist - which is why this returns the number it filed as and why a publication
/// that fails is carried as a park rather than swallowed.
/// </para>
/// </remarks>
public interface IIssuePublisher
{
    /// <summary>
    /// Files whatever the proposal asks a human to be given, and returns the issue it was
    /// filed as - null for a <see cref="WorkItemKind.Build"/>, which asks for nothing.
    /// </summary>
    /// <param name="workItem">The work item the verdict is about.</param>
    /// <param name="proposal">The verdict, as the round wrote it.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    Task<int?> PublishAsync(
        WorkItem workItem,
        RoutingProposal proposal,
        CancellationToken cancellationToken);
}
