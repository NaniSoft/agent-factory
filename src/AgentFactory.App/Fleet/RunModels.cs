using System.Text.Json.Serialization;

namespace AgentFactory.App.Fleet;

/// <summary>A run's lifecycle, as the orchestrator sees it.</summary>
public enum RunState
{
    /// <summary>Opted-in issue seen; waiting for a run slot.</summary>
    Queued,

    /// <summary>Session in flight (attempt 1 or the retry).</summary>
    Working,

    /// <summary>PR opened and the run recorded. Terminal.</summary>
    Succeeded,

    /// <summary>Both attempts exhausted; failure comment posted. Terminal.</summary>
    Failed,

    /// <summary>Queued run cancelled before it started — its target was
    /// un-enrolled (ticket 018), it sat downstream of a terminally failed
    /// sibling (ticket 020), or its parent was already gone. Terminal;
    /// history is kept.</summary>
    Cancelled,

    /// <summary>Label was removed while the run was still in flight (orphaned
    /// by a crash); treated as off the frontier until the label returns.</summary>
    Disarmed,

    /// <summary>Triage verdict was `cross`: the original was commented and
    /// closed as a duplicate of the routing issue in the org's `.github`,
    /// which now carries the work (ticket 019). Terminal for this run — the
    /// routed issue becomes a parent run (ticket 020). Appended AFTER the
    /// v1 values: states persist as numbers, so new ones must never insert
    /// mid-sequence (RunStore migrates the 019-era shift on load).</summary>
    Routed,
}

/// <summary>What a run is: an ordinary single-repo pipeline run, a parent run
/// on a cross-repo ticket (ticket 019 marks, ticket 020 executes), or a child
/// run a parent spawned — an ordinary pipeline run over a synthetic issue
/// (the planning session's per-child prompt), never dequeued by the fleet
/// queue itself.</summary>
public enum RunKind
{
    Normal,
    Parent,
    Child,
}

/// <summary>
/// One phase of a run as it actually executed: the engine session that walked
/// it, its timing, cost, and final reply (ticket 016 observability). Phases
/// render as steps in the run log and as rows on the run detail page.
/// </summary>
public sealed class PhaseRecord
{
    public string Name { get; set; } = "";
    public List<string> Skills { get; set; } = [];
    public string? SessionId { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public decimal Cost { get; set; }
    public long TokensIn { get; set; }
    public long TokensOut { get; set; }
    public string? Summary { get; set; }

    /// <summary>The profile pin this phase ran under, as resolved at run start
    /// (ticket 030); null = engine default, exactly the pre-profiles behavior.
    /// <see cref="ModelId"/> is what the engine read back from the session —
    /// truth over intent.</summary>
    public string? ProfileName { get; set; }
    public string? Flavour { get; set; }
    public string? ModelId { get; set; }
    public string? ProviderId { get; set; }
}

/// <summary>
/// One orchestrator run: the end-to-end handling of a single issue
/// (poll → session(s) → commit → PR → outcome), persisted for the dashboard.
/// </summary>
public sealed class RunRecord
{
    public string RunId { get; set; } = RunIds.New();

    /// <summary>The fleet target this run belongs to (owner/name) — its repo
    /// key across history: issue numbers collide across repos (ticket 017).
    /// Legacy v1 runs migrate to the playground repo on load.</summary>
    public string Repo { get; set; } = "";
    public int IssueNumber { get; set; }
    public string IssueTitle { get; set; } = "";
    public string IssueBody { get; set; } = "";
    public string IssueUrl { get; set; } = "";
    public RunState State { get; set; } = RunState.Queued;

    /// <summary>
    /// True when the opt-in label is currently absent from this run's issue:
    /// the run is armed, and the next poll that sees the label back enqueues a
    /// fresh run (the remove/re-add re-trigger contract).
    /// </summary>
    public bool Rearmed { get; set; }
    public int Attempt { get; set; }

    /// <summary>When the executor picked this run up — the round-robin
    /// queue's fairness clock (ticket 018); null while still queued. Legacy
    /// runs predate the field and read as never-started.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>Normal pipeline run vs parent run on a cross-repo ticket.</summary>
    public RunKind Kind { get; set; } = RunKind.Normal;

    /// <summary>Triage verdict at intake on org targets (ticket 019):
    /// "single" (override label or session) or "cross"; null when triage
    /// hasn't run (not an org target, or pre-019 runs).</summary>
    public string? TriageVerdict { get; set; }

    /// <summary>The eligible repos a cross verdict named (filtered to the
    /// org's eligible set; unknown names the triage session mentioned are
    /// logged, not recorded). Null unless the verdict was cross.</summary>
    public List<string>? TriageRepos { get; set; }

    /// <summary>One-line triage provenance for the dashboard (override label
    /// used, session cost, flagged names).</summary>
    public string? TriageSummary { get; set; }

    /// <summary>The triage session's id (dashboard transcript pointer).</summary>
    public string? TriageSessionId { get; set; }

    /// <summary>For routed runs: the `.github` issue that now carries the
    /// work (the run's Routed state points here).</summary>
    public string? RoutedIssueUrl { get; set; }

    /// <summary>On child runs (ticket 020): the parent run that spawned
    /// them. Parents hold the run slot and drive children sequentially.</summary>
    public string? ParentRunId { get; set; }

    /// <summary>On child runs: position in the parent's plan (0-based) —
    /// execution and merge order.</summary>
    public int ChildOrder { get; set; }

    /// <summary>On parent runs: the `.github` issue comment id of the
    /// tracking comment, updated as children land.</summary>
    public long? TrackingCommentId { get; set; }

    /// <summary>The pipeline this run walked, as it was named when the run
    /// started (the definition itself is snapshotted in memory for the walk;
    /// edits never reach an in-flight run — ticket 015).</summary>
    public string? PipelineName { get; set; }

    /// <summary>One entry per phase executed this attempt; rewritten wholesale
    /// on retry (a retry is a full fresh pipeline pass).</summary>
    public List<PhaseRecord> Phases { get; set; } = [];

    /// <summary>Current or last phase's engine session — the dashboard's live
    /// pointer; full per-phase detail is in <see cref="Phases"/>.</summary>
    public string? SessionId { get; set; }
    public string? Branch { get; set; }
    public string? WorktreePath { get; set; }
    public string? PrUrl { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string? LogPath { get; set; }
    public string? AgentSummary { get; set; }
}

public static class RunIds
{
    private static int _seq;
    public static string New() =>
        $"run-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Interlocked.Increment(ref _seq):D2}";
}

/// <summary>One opt-in issue as the poller saw it.</summary>
public sealed record IssueSnapshot(
    int Number,
    string Title,
    string Body,
    string Url,
    DateTimeOffset UpdatedAt);

/// <summary>The final assistant message of a session, as the host collected it.
/// The model fields carry the engine's readback (ticket 030) — null when the
/// session ran unpinned.</summary>
public sealed record SessionResult(
    string SessionId,
    string Text,
    decimal Cost,
    long TokensIn,
    long TokensOut,
    string? ModelId = null,
    string? ProviderId = null);
