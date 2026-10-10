namespace AgentFactory.WorkItems;

/// <summary>
/// What kind of work a work item turned out to be. A work item is an issue at intake and
/// stays an issue until a routing round has looked at it, and this is what that round said
/// (ADR-0013).
/// </summary>
/// <remarks>
/// <para>
/// The five are not a state machine and none of them is reached by the loop's own decision.
/// <see cref="Unrouted"/> is what a work item is at intake, because "nobody has looked at
/// this yet" is a fact and not a verdict — and a work item that has never been routed is
/// rendered on the board as its own state rather than as a <see cref="Build"/> that has not
/// got round to saying so.
/// </para>
/// <para>
/// This type and <see cref="WorkItemKinds"/> are the judgement the work-item detail's API
/// serialises through <c>HowToReadTheRoute</c>. The routing pipeline that would set a
/// verdict (ADR-0013: a routing round per open issue) is not in this branch, so every work
/// item is <see cref="Unrouted"/> and the route a detail shows is honestly "nobody has
/// looked at this issue yet". Nothing here invents a verdict.
/// </para>
/// </remarks>
public enum WorkItemKind
{
    /// <summary>
    /// No routing round has come back for this work item. The default a work item is born
    /// with and the only kind a <see cref="Build"/> verdict has to be told apart from.
    /// </summary>
    Unrouted,

    /// <summary>
    /// The issue is already a build ticket: specific enough to hand to a worker container
    /// and review afterwards.
    /// </summary>
    Build,

    /// <summary>
    /// Implementable, but branches of the design are undecided and only a human can decide
    /// them. Parks for a human, which is a different shape of waiting than
    /// <see cref="Wayfinder"/>'s and not a lesser kind of work.
    /// </summary>
    Grill,

    /// <summary>
    /// Bigger than one round and wrapped in fog: the way from here to the destination is
    /// not visible, so it is charted as a map of decision tickets rather than built.
    /// </summary>
    Wayfinder,
}

/// <summary>The kind's own words, for a card and a log line.</summary>
public static class WorkItemKinds
{
    /// <summary>
    /// Only <see cref="WorkItemKind.Build"/> is ever started. The other two park, because
    /// a map and a question are not things a container can build, and a work item that was
    /// routed <c>Grill</c> is not a work item a reviewer should be offered a diff of.
    /// </summary>
    public static bool IsBuildable(WorkItemKind kind) => kind == WorkItemKind.Build;

    public static string Label(WorkItemKind kind) => kind switch
    {
        WorkItemKind.Grill => "needs grilling",
        WorkItemKind.Wayfinder => "needs a wayfinder map",
        _ => kind.ToString(),
    };
}
