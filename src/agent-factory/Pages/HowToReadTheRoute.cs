namespace AgentFactory.Pages;

using AgentFactory.WorkItems;

/// <summary>
/// What a card says about the routing round that looked at its issue, in words rather than
/// in a state the reviewer has to already know the meaning of (ADR-0013).
/// </summary>
/// <remarks>
/// <para>
/// The judgement this holds is small and it is the whole of it: <b>a card must not let
/// "nobody has looked at this yet" read as "this is ready to build"</b>. Those are the two
/// facts a card's header otherwise puts next to each other — every work item is in Backlog,
/// and every Backlog work item is either waiting to be routed or waiting to be accepted — so
/// a board that renders both the same way is a board whose queue means nothing.
/// </para>
/// <para>
/// So every kind gets its own sentence, including the two that are not verdicts. There is
/// deliberately no default and no "unknown": a kind this build does not know is rendered as
/// the kind it is not, because a card that has to be read to find out that its state is
/// unreadable is a card nobody will read.
/// </para>
/// </remarks>
public static class HowToReadTheRoute
{
    /// <summary>
    /// The one sentence for this work item's kind, in the reviewer's terms. Never empty and
    /// never the same sentence for two kinds.
    /// </summary>
    public static string Say(WorkItem workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        return workItem.Kind switch
        {
            WorkItemKind.Unrouted when workItem.Swimlane == Swimlane.Escalated =>
                "A routing round looked at this issue and could not answer. It is parked with the reason above.",

            WorkItemKind.Unrouted =>
                "Nobody has looked at this issue yet. A routing round will decide whether it is a build "
                    + "ticket before it can be built, and it costs a container.",

            WorkItemKind.Build =>
                "A routing round read this and it is already a build ticket. Nothing has been built yet; "
                    + "the button below starts it.",

            WorkItemKind.Grill =>
                "A routing round read this and it needs deciding with a person before it can be built. "
                    + "Nothing will be spent on a container until somebody answers.",

            WorkItemKind.Wayfinder =>
                "A routing round read this and the way to the destination is not visible. It has been charted "
                    + "as a map of decisions rather than built, because building it now would be building the "
                    + "wrong thing.",

            _ => "This issue's kind is not one this factory knows how to act on, and it has not been built.",
        };
    }

    /// <summary>
    /// Whether a work item's card should be offering the acceptance button at all. The view
    /// asks this rather than writing the condition inline, so "the button is offered only for
    /// something the factory is willing to build" is one sentence in one file rather than a
    /// rule duplicated in markup and in the loop that enforces it.
    /// </summary>
    public static bool OffersAcceptance(WorkItem workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        return workItem.Swimlane == Swimlane.Backlog && WorkItemKinds.IsBuildable(workItem.Kind);
    }
}
