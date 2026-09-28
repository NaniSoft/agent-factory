namespace AgentFactory.WorkItems;

/// <summary>
/// Where a work item sits. Backlog, Frontier, In Progress, Review and Done are the
/// five swimlanes. Escalated is a parking state a human can still finish; Rejected is
/// final. Both are rendered, because nothing ends in silence.
/// </summary>
public enum Swimlane
{
    Backlog,
    Frontier,
    InProgress,
    Review,
    Done,
    Escalated,
    Rejected,
}

/// <summary>The five swimlanes, in the order they are rendered.</summary>
public static class Swimlanes
{
    public static readonly IReadOnlyList<Swimlane> All =
    [
        Swimlane.Backlog,
        Swimlane.Frontier,
        Swimlane.InProgress,
        Swimlane.Review,
        Swimlane.Done,
    ];

    /// <summary>Terminal and non-terminal, both rendered. Escalated is parked, not final.</summary>
    public static readonly IReadOnlyList<Swimlane> ParkedAndFinal =
    [
        Swimlane.Escalated,
        Swimlane.Rejected,
    ];

    public static string Label(Swimlane swimlane) => swimlane switch
    {
        Swimlane.InProgress => "In Progress",
        _ => swimlane.ToString(),
    };
}
