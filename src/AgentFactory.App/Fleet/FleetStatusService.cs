namespace AgentFactory.App.Fleet;

/// <summary>
/// Cheap runtime observability for the Targets page (ticket 022): when the
/// poller last touched each repo, and with what outcome note. In-memory only
/// — it describes the live process, not history.
/// </summary>
public sealed class FleetStatusService
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (DateTimeOffset At, string? Note)> _lastPoll = [];

    public void RecordPoll(string repo, string? note = null)
    {
        lock (_lock)
        {
            _lastPoll[repo] = (DateTimeOffset.UtcNow, note);
        }
    }

    /// <summary>Last poll of a repo (any outcome), null when never polled this
    /// process — the Targets page's "first scan running" signal on enroll.</summary>
    public (DateTimeOffset At, string? Note)? LastPoll(string repo)
    {
        lock (_lock)
        {
            return _lastPoll.TryGetValue(repo, out var hit) ? hit : null;
        }
    }
}
