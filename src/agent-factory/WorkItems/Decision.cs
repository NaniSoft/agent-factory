namespace AgentFactory.WorkItems;

/// <summary>
/// The three decisions, and only the three: what a reviewer does with a work item in
/// Review. There is no fourth, and something the board cannot read as one of these is
/// refused rather than guessed at — a fourth way out of Review is exactly what this
/// type exists to make impossible.
/// </summary>
public enum Decision
{
    /// <summary>The change is right. It goes to Done, and the merger takes it from there.</summary>
    Approve,

    /// <summary>
    /// The change is wrong in ways the reviewer can say out loud. The reasons travel
    /// with the work item and are the next round's brief.
    /// </summary>
    RequestChanges,

    /// <summary>The change is declined. Rejected is final, and no decision undoes it.</summary>
    Reject,
}

/// <summary>
/// The three as the board offers them: the slug a button posts, and the label a reviewer
/// reads. Rendering the board's buttons from here rather than writing them out by hand
/// is what keeps "exactly three" true as a fact about the write path and not only about
/// the markup.
/// </summary>
public static class Decisions
{
    public static readonly IReadOnlyList<Decision> All =
    [
        Decision.Approve,
        Decision.RequestChanges,
        Decision.Reject,
    ];

    /// <summary>What a button posts, and so what the board's form carries.</summary>
    public static string Slug(Decision decision) => decision switch
    {
        Decision.Approve => "approve",
        Decision.RequestChanges => "request-changes",
        Decision.Reject => "reject",
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "there are only three decisions"),
    };

    public static string Label(Decision decision) => decision switch
    {
        Decision.Approve => "Approve",
        Decision.RequestChanges => "Request changes",
        Decision.Reject => "Reject",
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "there are only three decisions"),
    };

    /// <summary>
    /// Reads a decision out of what a button posted, by slug and case-insensitively,
    /// because a slug is the wire format and a reviewer's keyboard is not a schema. A
    /// value that is not one of the three is not a decision: nothing here parses an
    /// arbitrary name or a bare number into the set, so the set cannot grow by accident.
    /// </summary>
    public static bool TryParse(string? posted, out Decision decision)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(Slug(candidate), posted, StringComparison.OrdinalIgnoreCase))
            {
                decision = candidate;
                return true;
            }
        }

        decision = default;
        return false;
    }
}
