namespace AgentFactory.Infrastructure.WorkItems;

using AgentFactory.Domain.WorkItems;

/// <summary>
/// One row of <c>work_items</c>: the schema's shape rather than the port's.
/// </summary>
/// <remarks>
/// <para>
/// It is a mutable bag of scalars with one column per property, and it is deliberately
/// <em>not</em> <see cref="WorkItem"/>. The alternative — mapping the domain record itself
/// as the entity — was refused for a reason that is the whole of this folder's design: the
/// records are the <strong>port's vocabulary</strong>, they are immutable positional
/// records, and the table is a schema free to differ from them. Mapping
/// <c>RoundResultRecord</c> is not even possible, because its one value object is
/// deliberately five independently-nullable columns rather than a shape mapped as a unit;
/// a model in which three tables' entities are the port's records and one is a row type is
/// a model with an exception nobody can state in a sentence, so all four are row types and
/// the mapping lives in one place — <see cref="RowMapping"/>.
/// </para>
/// <para>
/// Nothing outside this assembly sees one: the model is <c>Infrastructure</c>'s own, which
/// is what keeps `Application` free of EF while <c>IWorkItemStore</c> stays a port.
/// </para>
/// </remarks>
internal sealed class WorkItemRow
{
    public Guid Id { get; set; }

    public string Project { get; set; } = string.Empty;

    public string RepoUrl { get; set; } = string.Empty;

    public int IssueNumber { get; set; }

    public string IssueTitle { get; set; } = string.Empty;

    public string IssueBody { get; set; } = string.Empty;

    public string BaseBranch { get; set; } = string.Empty;

    public Swimlane Swimlane { get; set; }

    public int RoundCount { get; set; }

    /// <summary>
    /// When the work item was made. One of the four columns carrying a `long` tick
    /// converter, because the Board orders by it on every poll and SQLite's provider
    /// refuses `ORDER BY` over a `DateTimeOffset`.
    /// </summary>
    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>When it was last written. Stays readable text: nothing sorts or compares it.</summary>
    public DateTimeOffset UpdatedUtc { get; set; }

    /// <summary>
    /// When the current review began, and null whenever the work item is not in Review.
    /// Stays readable text, deliberately: the feedback threshold is compared against it in
    /// the loop, over a record it already holds, and there is no query that orders or
    /// compares this column.
    /// </summary>
    public DateTimeOffset? ReviewStartedUtc { get; set; }

    public int MergeAttempts { get; set; }

    /// <summary>When another merge attempt may be made. Readable text, for the same reason.</summary>
    public DateTimeOffset? MergeRetryAfterUtc { get; set; }

    public WorkItemKind Kind { get; set; }

    public string? RouteReason { get; set; }

    public int? RoutingIssue { get; set; }
}
