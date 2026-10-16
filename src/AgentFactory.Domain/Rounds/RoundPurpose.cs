namespace AgentFactory.Domain.Rounds;

using AgentFactory.Domain.WorkItems;

/// <summary>
/// What a round is for. Two of them, and the difference is whether the round looks at the
/// repository.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="Build"/> round is the one this factory had before the workflow was embedded:
/// it fetches the repository and hands the agent something to build.
/// </para>
/// <para>
/// A <see cref="Route"/> round is one container spent reading an issue's own words to decide
/// what kind of work it is. It is a per-purpose round rather than a flag on a build round
/// because it is cheaper in the only way that matters — it does not clone — and because the
/// two rounds produce different files and are read by different components. A round that
/// both built and routed would make "what did this round decide" and "what did this round
/// change" one call with two answers.
/// </para>
/// </remarks>
public enum RoundPurpose
{
    /// <summary>Build the work item. Fetches the repository, commits, and lands a diff.</summary>
    Build,

    /// <summary>
    /// Decide what kind of work the work item is. Reads the issue's title and body, writes
    /// a verdict, and does not fetch anything.
    /// </summary>
    Route,
}

/// <summary>
/// Whether a round of this purpose fetches the work item's repository. One place, because
/// the answer decides whether a round spends a container cloning a repository it is not going
/// to read, and that is worth not getting wrong in either direction.
/// </summary>
public static class RoundPurposes
{
    public static bool FetchesTheRepository(RoundPurpose purpose) => purpose == RoundPurpose.Build;

    /// <summary>
    /// Whether the budget this round spends is worth what it returns. A routing round costs
    /// a container per open issue and returns one word of judgement; a build round costs one
    /// and returns a change. Both are drawn from the same budget — the alternative is a
    /// second budget, and a second budget is a second thing a reviewer has to understand.
    /// </summary>
    public static bool IsRoutable(WorkItemKind kind) => kind == WorkItemKind.Unrouted;
}