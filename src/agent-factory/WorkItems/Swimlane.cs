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

    /// <summary>
    /// Approved **and merged**, which is what the design says this lane means. The merger
    /// is the only way in, so a work item in Done is a claim about a repository rather
    /// than about a reviewer's opinion: an approval the loop could not carry out is not
    /// Done, because a board that reports a merge that did not happen is the failure this
    /// factory exists to prevent.
    /// </summary>
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

    /// <summary>
    /// The lanes a reviewer can still decide about: the one they are waiting on, and the
    /// one they have parked.
    /// </summary>
    /// <remarks>
    /// Escalated is in here because escalation parks a work item rather than graving it:
    /// a human finishes it from the board, by merging it or declining it, and a store
    /// that refused a decision about anything but Review would make that impossible
    /// (ADR-0008). Rejected is deliberately absent and cannot be added, which is what
    /// makes a decline final without a later caller having to remember.
    /// </remarks>
    public static readonly IReadOnlyList<Swimlane> Decidable =
    [
        Swimlane.Review,
        Swimlane.Escalated,
    ];

    public static string Label(Swimlane swimlane) => swimlane switch
    {
        Swimlane.InProgress => "In Progress",
        _ => swimlane.ToString(),
    };
}
