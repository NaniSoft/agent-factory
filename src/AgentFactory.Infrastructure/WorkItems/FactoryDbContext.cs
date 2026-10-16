namespace AgentFactory.Infrastructure.WorkItems;

using AgentFactory.Domain.WorkItems;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

/// <summary>
/// The store of record as an EF Core model: four tables, one row type each, and the whole
/// of the mapping written out here.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is `Infrastructure`'s and nothing else's.</strong> <c>IWorkItemStore</c> is
/// still a port declared in `Application`, its records are still the port's shapes, and no
/// EF type is named on the far side of the boundary — which is the property the map asked
/// for (#62, #77).
/// </para>
/// <para>
/// The schema is the one the hand-written ADO.NET store defined, column for column, so
/// that nothing about a fresh database changes except how it is made. Two things are
/// deliberately different from what EF would do unprompted, and both are recorded at the
/// property that decides them: **enums are stored as text**, and **no foreign keys are
/// created**.
/// </para>
/// <para>
/// A context is created per operation and thrown away. That is not thrift — it is what
/// keeps a write that failed on the unique index from leaving a tracked, half-inserted
/// entity behind for the next `SaveChanges` on the same context to try again.
/// </para>
/// </remarks>
public sealed class FactoryDbContext(DbContextOptions<FactoryDbContext> options) : DbContext(options)
{
    internal DbSet<WorkItemRow> WorkItems => Set<WorkItemRow>();

    internal DbSet<RoundResultRow> RoundResults => Set<RoundResultRow>();

    internal DbSet<DecisionRow> Decisions => Set<DecisionRow>();

    internal DbSet<PendingRoundRow> PendingRounds => Set<PendingRoundRow>();

    /// <summary>
    /// A moment, as the count of ticks since the epoch in UTC.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This exists because SQLite's provider refuses `DateTimeOffset` in an
    /// `ORDER BY` or a comparison</strong> — it throws `NotSupportedException` rather than
    /// evaluating on the client — and two of this store's reads do exactly that: the Board
    /// orders every work item by when it was created on every five-second poll, and the
    /// reconciliation list orders pending rounds by when they were asked for.
    /// </para>
    /// <para>
    /// It is applied to <see cref="WorkItemRow.CreatedUtc"/> and
    /// <see cref="PendingRoundRow.AskedUtc"/> and to nothing else. Every other timestamp
    /// stays text, because the columns that have to be *sorted* and the columns that have
    /// to be *read* are not the same set, and an operator with the `sqlite3` CLI can still
    /// read `updated_utc`, `started_utc`, `review_started_utc` and the rest of them.
    /// </para>
    /// <para>
    /// `UtcTicks` normalises the offset away, which is safe here and worth saying: every
    /// moment written to this store comes from <c>IClock.UtcNow</c>, which is
    /// <c>DateTimeOffset.UtcNow</c>, so the offset was always `+00:00` and the round trip
    /// gives back the instant that went in.
    /// </para>
    /// </remarks>
    private static readonly ValueConverter<DateTimeOffset, long> Ticks =
        new(moment => moment.UtcTicks, ticks => new DateTimeOffset(ticks, TimeSpan.Zero));

    /// <summary>
    /// A work item's kind, as the word it is stored under — with the fallback the store has
    /// always had.
    /// </summary>
    /// <remarks>
    /// A kind this build does not recognise reads as <see cref="WorkItemKind.Unrouted"/>
    /// rather than throwing, and the reason is the column's default: an unknown verdict is
    /// a verdict nobody made, which is the one that means "not looked at yet", and the next
    /// routing round overwrites it with a real one. EF's own `HasConversion&lt;string&gt;()`
    /// would `Enum.Parse` it and refuse, which would turn a column's worth of unreadable
    /// text into a store that cannot be read at all. The other four enum columns keep the
    /// plain conversion: the store has never had a fallback for them, and inventing one now
    /// would be moving policy.
    /// </remarks>
    private static readonly ValueConverter<WorkItemKind, string> KindAsWord =
        new(kind => kind.ToString(), word => KindFromWord(word));

    private static WorkItemKind KindFromWord(string word) =>
        Enum.TryParse<WorkItemKind>(word, out var kind) ? kind : WorkItemKind.Unrouted;

    protected override void OnModelCreating(ModelBuilder model)
    {
        // Every entity is named here rather than discovered from a `DbSet` property, so
        // that a row type added to this assembly is a compile-time absence rather than a
        // table that quietly stops being mapped.
        ConfigureWorkItems(model);
        ConfigureRoundResults(model);
        ConfigureDecisions(model);
        ConfigurePendingRounds(model);
    }

    private static void ConfigureWorkItems(ModelBuilder model) => model.Entity<WorkItemRow>(row =>
    {
        row.ToTable("work_items");
        row.HasKey(item => item.Id);
        row.Property(item => item.Id).HasColumnName("id");
        row.Property(item => item.Project).HasColumnName("project");
        row.Property(item => item.RepoUrl).HasColumnName("repo_url");
        row.Property(item => item.IssueNumber).HasColumnName("issue_number");
        row.Property(item => item.IssueTitle).HasColumnName("issue_title");
        row.Property(item => item.IssueBody).HasColumnName("issue_body");
        row.Property(item => item.BaseBranch).HasColumnName("base_branch");
        row.Property(item => item.Swimlane).HasColumnName("swimlane").HasConversion<string>();
        row.Property(item => item.RoundCount).HasColumnName("round_count");
        row.Property(item => item.CreatedUtc).HasColumnName("created_utc").HasConversion(Ticks);
        row.Property(item => item.UpdatedUtc).HasColumnName("updated_utc");
        row.Property(item => item.ReviewStartedUtc).HasColumnName("review_started_utc");
        row.Property(item => item.MergeAttempts).HasColumnName("merge_attempts").HasDefaultValue(0);
        row.Property(item => item.MergeRetryAfterUtc).HasColumnName("merge_retry_after_utc");
        row.Property(item => item.Kind)
            .HasColumnName("kind")
            .HasConversion(KindAsWord)
            .HasDefaultValue(WorkItemKind.Unrouted);
        row.Property(item => item.RouteReason).HasColumnName("route_reason");
        row.Property(item => item.RoutingIssue).HasColumnName("routing_issue");

        // Intake's idempotency, and the whole of it. Nothing checks whether an issue is
        // already there before writing, because a check and a write can disagree and the
        // write is the only one of the two that is atomic.
        row.HasIndex(item => new { item.RepoUrl, item.IssueNumber })
            .IsUnique()
            .HasDatabaseName("ix_work_items_source_issue");
    });

    private static void ConfigureRoundResults(ModelBuilder model) => model.Entity<RoundResultRow>(row =>
    {
        row.ToTable("round_results");
        row.HasKey(result => new { result.WorkItemId, result.RoundNumber });
        row.Property(result => result.WorkItemId).HasColumnName("work_item_id");
        row.Property(result => result.RoundNumber).HasColumnName("round_number");
        row.Property(result => result.Outcome).HasColumnName("outcome").HasConversion<string>();
        row.Property(result => result.ResultPayload).HasColumnName("result_payload");
        row.Property(result => result.AgentNote).HasColumnName("agent_note");
        row.Property(result => result.StartedUtc).HasColumnName("started_utc");
        row.Property(result => result.CompletedUtc).HasColumnName("completed_utc");
        row.Property(result => result.Attempts).HasColumnName("attempts").HasDefaultValue(1);
        row.Property(result => result.Failure).HasColumnName("failure").HasConversion<string>();
        row.Property(result => result.Log).HasColumnName("log");
        row.Property(result => result.Diff).HasColumnName("diff");
        row.Property(result => result.DiffBase).HasColumnName("diff_base");
        row.Property(result => result.DiffTree).HasColumnName("diff_tree");
        row.Property(result => result.DiffContainerBounded)
            .HasColumnName("diff_container_bounded")
            .HasDefaultValue(false);
        row.Property(result => result.DiffUnavailable).HasColumnName("diff_unavailable");

        // No navigation property and no relationship, so no foreign key is emitted. The
        // hand-written schema had none, and adding one would be a new refusal rather than
        // the same rule kept: nothing in this factory deletes a work item, and a cascade
        // would mean that if something ever did, it would take the rounds and the decisions
        // with it — the record of record would be the thing deleted first.
    });

    private static void ConfigureDecisions(ModelBuilder model) => model.Entity<DecisionRow>(row =>
    {
        row.ToTable("decisions");
        row.HasKey(decision => new { decision.WorkItemId, decision.Sequence });
        row.Property(decision => decision.WorkItemId).HasColumnName("work_item_id");
        row.Property(decision => decision.Sequence).HasColumnName("sequence");
        row.Property(decision => decision.Decision).HasColumnName("decision").HasConversion<string>();
        row.Property(decision => decision.Feedback).HasColumnName("feedback");
        row.Property(decision => decision.DecidedUtc).HasColumnName("decided_utc");
        row.Property(decision => decision.AppliedTo).HasColumnName("applied_to").HasConversion<string>();

        // Kept although the composite primary key already indexes these two columns, and
        // kept deliberately: it is the schema the store of record has today, and a move
        // that silently dropped an index would be a schema change nobody asked for.
        row.HasIndex(decision => new { decision.WorkItemId, decision.Sequence })
            .HasDatabaseName("ix_decisions_work_item");
    });

    private static void ConfigurePendingRounds(ModelBuilder model) => model.Entity<PendingRoundRow>(row =>
    {
        row.ToTable("pending_rounds");
        row.HasKey(pending => pending.WorkItemId);
        row.Property(pending => pending.WorkItemId).HasColumnName("work_item_id");
        row.Property(pending => pending.RoundNumber).HasColumnName("round_number");
        row.Property(pending => pending.Attempt).HasColumnName("attempt");
        row.Property(pending => pending.Purpose).HasColumnName("purpose");
        row.Property(pending => pending.AskedUtc).HasColumnName("asked_utc").HasConversion(Ticks);
        row.Property(pending => pending.RetryAfterUtc).HasColumnName("retry_after_utc");

        // As above: the schema had no foreign key, and none is created.
    });
}
