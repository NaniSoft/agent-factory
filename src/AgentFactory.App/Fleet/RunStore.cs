using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentFactory.App.Fleet;

/// <summary>
/// Run history, persisted as JSON (data/runs.json) with atomic replace writes.
/// The dashboard reads this; it is the only place run state lives.
/// </summary>
public sealed class RunStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock _lock = new();
    private readonly string _storePath;
    private readonly string _logDir;
    private readonly ConcurrentDictionary<string, RunRecord> _runs = new();
    private readonly Dictionary<string, StreamWriter> _logWriters = [];

    public RunStore(string dataDir, string legacyDefaultRepo)
    {
        Directory.CreateDirectory(dataDir);
        _storePath = Path.Combine(dataDir, "runs.json");
        _logDir = Path.Combine(dataDir, "runs");
        Directory.CreateDirectory(_logDir);

        var migrated = false;
        if (File.Exists(_storePath))
        {
            foreach (var run in JsonSerializer.Deserialize<List<RunRecord>>(File.ReadAllText(_storePath)) ?? [])
            {
                // v1 runs predate target identity; they all ran on the
                // playground — backfill so re-trigger semantics survive.
                if (string.IsNullOrEmpty(run.Repo))
                {
                    run.Repo = legacyDefaultRepo;
                    migrated = true;
                }
                // 019 briefly inserted Routed between Failed and Cancelled,
                // shifting the persisted numbers. Routes are distinguishable
                // (they always carry RoutedIssueUrl) and cancels always carry
                // an Error; restore the true states under the append-only
                // ordering (Cancelled=4, Disarmed=5, Routed=6). The branches
                // only fire on 019-era shapes, so the fix-up is idempotent.
                if ((int)run.State == 4 && run.RoutedIssueUrl is not null)
                {
                    run.State = RunState.Routed; // 019-era Routed → 6
                    migrated = true;
                }
                else if ((int)run.State == 5 && run.Error is not null)
                {
                    run.State = RunState.Cancelled; // 019-era Cancelled → 4
                    migrated = true;
                }
                else if ((int)run.State == 6 && run.RoutedIssueUrl is null)
                {
                    run.State = RunState.Disarmed; // 019-era Disarmed → 5
                    migrated = true;
                }
                _runs[run.RunId] = run;
            }
            if (migrated) PersistLocked();
        }
    }

    public IReadOnlyList<RunRecord> All
    {
        get { lock (_lock) return [.. _runs.Values.OrderByDescending(r => r.CreatedAt)]; }
    }

    public RunRecord? FindByIssue(string repo, int issueNumber) =>
        All.FirstOrDefault(r => r.Repo == repo && r.IssueNumber == issueNumber);

    /// <summary>Whether this issue should be (re)enqueued given its current
    /// label state on its repo.</summary>
    public bool NeedsRun(string repo, int issueNumber)
    {
        var existing = FindByIssue(repo, issueNumber);
        return existing is null || existing.State == RunState.Disarmed || existing.Rearmed;
    }

    public void Add(RunRecord run)
    {
        lock (_lock)
        {
            _runs[run.RunId] = run;
            PersistLocked();
        }
    }

    public void Update(RunRecord run)
    {
        lock (_lock)
        {
            _runs[run.RunId] = run;
            PersistLocked();
        }
    }

    /// <summary>
    /// Reconcile against the currently-labelled set, per repo: the newest run
    /// of every label-absent issue is armed for re-trigger (remove/re-add
    /// contract), and in-flight runs orphaned by a crash are Disarmed. Repos
    /// absent from the map weren't polled this cycle (disabled, `all` intake —
    /// ticket 018) and are left alone. An arm is consumed by a new run
    /// becoming the newest for that issue — never by the label merely being
    /// present, or the enqueue pass would never see it. Child runs never
    /// carry the label contract (their issues are synthetic, ticket 020).
    /// </summary>
    public void DisarmMissing(IReadOnlyDictionary<string, HashSet<int>> labelledByRepo)
    {
        lock (_lock)
        {
            foreach (var group in _runs.Values.Where(r => r.Kind != RunKind.Child).GroupBy(r => r.Repo))
            {
                if (!labelledByRepo.TryGetValue(group.Key, out var labelled)) continue;
                foreach (var issueGroup in group.GroupBy(r => r.IssueNumber))
                {
                    var newest = issueGroup.OrderByDescending(r => r.CreatedAt).First();
                    if (!labelled.Contains(newest.IssueNumber))
                    {
                        newest.Rearmed = true;
                        if (newest.State is RunState.Queued or RunState.Working)
                        {
                            newest.State = RunState.Disarmed; // crash-orphan recovery
                        }
                    }
                }
            }
            PersistLocked();
        }
    }

    private void PersistLocked()
    {
        var tmp = _storePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_runs.Values.ToList(), JsonOpts));
        File.Move(tmp, _storePath, overwrite: true);
    }

    /// <summary>Append a timestamped line to the run's log file (data/runs/&lt;runId&gt;.log).
    /// Writers allow concurrent reads so the dashboard can tail live runs.</summary>
    public void Log(RunRecord run, string message)
    {
        lock (_lock)
        {
            var path = Path.Combine(_logDir, run.RunId + ".log");
            if (!_logWriters.TryGetValue(run.RunId, out var writer))
            {
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                writer = new StreamWriter(stream) { AutoFlush = true };
                _logWriters[run.RunId] = writer;
            }
            writer.WriteLine($"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}  {message}");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var writer in _logWriters.Values) writer.Dispose();
            _logWriters.Clear();
        }
    }
}
