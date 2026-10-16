namespace AgentFactory.Domain.Routing;

using AgentFactory.Domain.WorkItems;

/// <summary>
/// What a routing round proposed, read out of <c>routing.json</c> by
/// <see cref="RoutingReader"/>. It is a proposal: where it lands is the host's decision,
/// because the host is where every write credential lives.
/// </summary>
/// <remarks>
/// <para>
/// The shape is the loop's own vocabulary — a verdict, the reason for it, and whichever of
/// two payloads that verdict implies — rather than the skills' vocabulary, and that is a
/// deliberate translation rather than a loss. <c>ask-matt</c> answers with "grill it" or
/// "wayfind it"; a state machine needs to be able to say what that means here, and what it
/// means here is a lane and an issue.
/// </para>
/// <para>
/// Nothing in this file is ever trusted into a work item's kind directly. A verdict this
/// cannot read is a park with a reason on it, and the factory never guesses a verdict — the
/// same rule the result deriver follows for a result it cannot read, for the same
/// reason: "this could not be read" and "this round decided nothing" are different claims.
/// </para>
/// </remarks>
/// <param name="Verdict">What the round said this issue is.</param>
/// <param name="Because">The round's own reason, kept whole. Required by the store.</param>
/// <param name="Questions">
/// The questions a human has to answer, for a <see cref="WorkItemKind.Grill"/>. Empty for
/// the other verdicts, and a <see cref="WorkItemKind.Grill"/> with none is refused by the
/// store rather than published as a question set with nothing in it.
/// </param>
/// <param name="Map">
/// The charted map, for a <see cref="WorkItemKind.Wayfinder"/>. Null for the other
/// verdicts.
/// </param>
public sealed record RoutingProposal(
    WorkItemKind Verdict,
    string Because,
    IReadOnlyList<ProposedQuestion> Questions,
    ProposedMap? Map)
{
    /// <summary>
    /// A verdict that needs a human to be asked something, and therefore needs an issue to
    /// ask them on. This is the single place that answers it, so a verdict cannot be
    /// published without somewhere to put the question.
    /// </summary>
    public bool AsksSomething =>
        Verdict is WorkItemKind.Grill or WorkItemKind.Wayfinder;
}

/// <summary>
/// One question for a human, and why it has to be asked. The question is the thing the
/// human reads; the reason is what lets a later round — or a person reading the issue
/// months later — tell a question that matters from one that was asked to look busy.
/// </summary>
/// <param name="Question">What is being asked, in the round's words.</param>
/// <param name="Because">Why the answer is needed and what it unblocks.</param>
public sealed record ProposedQuestion(string Question, string Because);

/// <summary>
/// A wayfinder map: what reaching the end of this effort looks like, and the decision
/// tickets it can already be split into.
/// </summary>
/// <remarks>
/// The three sections are the map's own index rather than its content, and the split is
/// carried because a map that could state its destination and nothing else would be a title.
/// <c>NotYetSpecified</c> is the fog — the decisions that are known to exist and cannot yet
/// be phrased sharply enough to be a ticket — and <c>OutOfScope</c> is the work ruled
/// beyond the destination, which is a different thing from fog and never graduates.
/// </remarks>
/// <param name="Destination">What reaching the end of this looks like.</param>
/// <param name="Notes">Domain, skills and standing preferences for this effort.</param>
/// <param name="NotYetSpecified">The fog, as far as it can be seen.</param>
/// <param name="OutOfScope">Work consciously ruled beyond the destination.</param>
/// <param name="Tickets">
/// The decision tickets, in dependency order: each one's <see cref="ProposedTicket.Blocks"/>
/// names tickets that come before it, by title.
/// </param>
public sealed record ProposedMap(
    string Destination,
    string Notes,
    IReadOnlyList<string> NotYetSpecified,
    IReadOnlyList<string> OutOfScope,
    IReadOnlyList<ProposedTicket> Tickets);

/// <summary>
/// One decision ticket inside a map. Its blocking edges are named by title rather than by
/// index because the host creates the issues in a second pass, in dependency order, and a
/// name is the only handle that exists before the issues have numbers.
/// </summary>
/// <param name="Title">What the decision is called. Also the issue's title.</param>
/// <param name="Body">The question this ticket resolves.</param>
/// <param name="Blocks">The titles of the tickets that cannot start until this one is answered.</param>
public sealed record ProposedTicket(string Title, string Body, IReadOnlyList<string> Blocks);