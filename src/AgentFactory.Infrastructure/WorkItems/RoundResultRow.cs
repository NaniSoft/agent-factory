namespace AgentFactory.Infrastructure.WorkItems;

using AgentFactory.Domain.Failures;
using AgentFactory.Domain.Results;
using AgentFactory.Domain.Rounds;

/// <summary>
/// One row of <c>round_results</c>: the schema's shape rather than the port's.
/// </summary>
/// <remarks>
/// <strong>The five diff columns are five columns here and stay five.</strong> `OwnsOne`
/// and a complex property were both refused: either would replace five independently
/// nullable columns with a shape that has to be read back as a unit, reintroducing the
/// "parses but is missing the field the reviewer needs" failure this store exists to
/// avoid (ADR-0009, and "The diff in Review" in AGENTS.md). The row therefore holds the
/// five scalars and <see cref="RoundResultRecord"/> is assembled from them in one place.
/// </remarks>
internal sealed class RoundResultRow
{
    public Guid WorkItemId { get; set; }

    public int RoundNumber { get; set; }

    public RoundOutcome Outcome { get; set; }

    public string? ResultPayload { get; set; }

    public string? AgentNote { get; set; }

    /// <summary>When the round started. Readable text: rounds are ordered by their number.</summary>
    public DateTimeOffset StartedUtc { get; set; }

    /// <summary>When it was recorded. Readable text, for the same reason.</summary>
    public DateTimeOffset CompletedUtc { get; set; }

    public int Attempts { get; set; }

    public FailureClass? Failure { get; set; }

    public string? Log { get; set; }

    /// <summary>The diff, as git wrote it. Null when the row has no diff at all.</summary>
    public string? Diff { get; set; }

    /// <summary>The commit the diff is against — where the round began.</summary>
    public string? DiffBase { get; set; }

    /// <summary>The lifted tree it was generated from, so a reviewer can go and look.</summary>
    public string? DiffTree { get; set; }

    /// <summary>Whether the container's own copy of the same change was bounded by the image.</summary>
    public bool DiffContainerBounded { get; set; }

    /// <summary>
    /// Why there is no diff at all, or null when there is one — including an empty one.
    /// The "there was no diff and here is why" shape, which is not an absence of change.
    /// </summary>
    public string? DiffUnavailable { get; set; }
}
