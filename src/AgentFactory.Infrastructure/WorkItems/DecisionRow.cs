namespace AgentFactory.Infrastructure.WorkItems;

using AgentFactory.Domain.WorkItems;

/// <summary>
/// One row of <c>decisions</c>: the schema's shape rather than the port's.
/// </summary>
internal sealed class DecisionRow
{
    public Guid WorkItemId { get; set; }

    /// <summary>Where it sits in the order the work item was decided in. Per work item.</summary>
    public int Sequence { get; set; }

    public Decision Decision { get; set; }

    /// <summary>What the reviewer wrote, kept whole.</summary>
    public string Feedback { get; set; } = string.Empty;

    /// <summary>When they decided. Readable text: decisions are ordered by their sequence.</summary>
    public DateTimeOffset DecidedUtc { get; set; }

    /// <summary>The swimlane the loop put the work item in, or null until it did.</summary>
    public Swimlane? AppliedTo { get; set; }
}
