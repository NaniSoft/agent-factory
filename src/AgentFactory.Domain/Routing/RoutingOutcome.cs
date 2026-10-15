namespace AgentFactory.Domain.Routing;

/// <summary>
/// What became of a routing round's verdict: either the proposal it wrote, or the reason it
/// could not be read. One of the two is always there, which is what makes "the round said
/// this is a build ticket" and "the round said nothing" two different events rather than one.
/// </summary>
/// <param name="Proposal">The verdict, or null when it could not be read.</param>
/// <param name="UnreadableBecause">
/// The round's own words for why there is no verdict, or null when there is one. Always the
/// exact opposite of <paramref name="Proposal"/>, so no caller has to decide which half it
/// is looking at.
/// </param>
/// <remarks>
/// It is the vocabulary, and it is `Domain`'s, for the reason <c>WorkItem</c> is: a verdict
/// is one of the factory's own words and a round's result carries it, so
/// <c>RoundResult</c> cannot be a thing <c>Domain</c> holds while the thing it carries lives
/// outside. Who reads it off disk is a separate question with a separate answer, and the
/// reader is an adapter (docs/architecture.md, "The dependency rule").
/// </remarks>
public sealed record RoutingOutcome(RoutingProposal? Proposal, string? UnreadableBecause)
{
    /// <summary>A verdict that was read, and says what it is.</summary>
    public static RoutingOutcome Read(RoutingProposal proposal) => new(proposal, null);

    /// <summary>No verdict, and the reason there is none. The work item parks on this.</summary>
    public static RoutingOutcome Unreadable(string because) => new(null, because);

    public bool IsReadable => Proposal is not null;
}
