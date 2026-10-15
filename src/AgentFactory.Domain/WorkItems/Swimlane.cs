namespace AgentFactory.Domain.WorkItems;

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

    /// <summary>
    /// Waiting on a person rather than on a container: a work item the factory has
    /// understood well enough to know it cannot finish unattended (ADR-0015).
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is a lane of its own rather than a variety of Escalated, and the difference is
    /// what the board may offer. Escalated is a parked <em>change</em>, so a human finishes
    /// it by merging it or declining it and those two decisions are real. A work item here
    /// has no change to judge at all — a map and a question set are not diffs — so offering
    /// it a diff-shaped decision would be offering to approve nothing.
    /// </para>
    /// <para>
    /// It is deliberately not terminal either. A human answers in the issue tracker and the
    /// factory picks the work back up from there, so this is a pause with an exit rather than
    /// a grave, and the difference from <see cref="Escalated"/> is exactly that: Escalated is
    /// finished by a human here and nowhere else.
    /// </para>
    /// </remarks>
    Waiting,

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
        Swimlane.Waiting,
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
    ///
    /// Waiting is absent for a different reason, and it is load-bearing. Escalated holds a
    /// parked <em>change</em>, so a human finishes it by merging or declining and both
    /// decisions are about something real. A work item in Waiting has no change to judge at
    /// all — a map and a question set are not diffs — so the two decisions above would both
    /// be decisions about nothing. It is answered in the issue tracker instead
    /// (ADR-0015), and the store refusing a decision about it is what keeps the board from
    /// ever rendering one.
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
