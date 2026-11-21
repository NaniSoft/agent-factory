using System.Text.Json;

namespace AgentFactory.App.Fleet;

/// <summary>
/// Read model for the dashboard: run history from the store, per-run log tails
/// from disk, and session/transcript lookups by id from the engine. Everything
/// degrades gracefully when the engine is down — the dashboard is read-only.
/// </summary>
public sealed class DashboardService(
    FleetOptions options, RunStore store, OpenCodeEngine engine, ConfigStore config)
{
    /// <summary>One rendered transcript entry: role plus text/tool markers.</summary>
    public sealed record TranscriptLine(DateTimeOffset Time, string Role, string Text);

    public IReadOnlyList<RunRecord> GetRuns() => store.All;

    public RunRecord? GetRun(string runId) => store.All.FirstOrDefault(r => r.RunId == runId);

    /// <summary>Enrolled fleet targets (ticket 017) — the intake card's count.</summary>
    public int GetTargetCount() => config.Targets().Count;

    public string? GetLogTail(string runId, int lines = 300)
    {
        var path = Path.Combine(options.DataDir, "runs", runId + ".log");
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return all.Length == 0 ? null : string.Join('\n', all.TakeLast(lines));
        }
        catch (IOException)
        {
            return null; // writer had the file mid-append; next tick retries
        }
    }

    public async Task<bool> EngineUpAsync(CancellationToken ct = default) =>
        engine.IsRunning && await engine.IsHealthyAsync(ct);

    /// <summary>Engine port and poll interval, for the dashboard header.</summary>
    public (int ServerPort, int PollSeconds) GetFleetInfo() => (options.ServerPort, options.PollIntervalSeconds);

    public Task<OpenCodeEngine.SessionSummary?> GetSessionAsync(string? sessionId, CancellationToken ct = default) =>
        sessionId is null ? Task.FromResult<OpenCodeEngine.SessionSummary?>(null) : engine.GetSessionByIdAsync(sessionId, ct);

    /// <summary>Parse the engine transcript JSON into renderable lines.</summary>
    public async Task<List<TranscriptLine>?> GetTranscriptAsync(string? sessionId, CancellationToken ct = default)
    {
        if (sessionId is null) return null;
        var json = await engine.GetTranscriptByIdAsync(sessionId, ct);
        if (json is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var lines = new List<TranscriptLine>();
            foreach (var message in doc.RootElement.EnumerateArray())
            {
                var role = message.GetProperty("info").GetProperty("role").GetString() ?? "?";
                var created = message.GetProperty("info").GetProperty("time").GetProperty("created").GetInt64();
                var time = DateTimeOffset.FromUnixTimeMilliseconds(created);
                var text = string.Join("\n", message.GetProperty("parts").EnumerateArray()
                    .Select(part => PartText(part, role))
                    .Where(t => t is not null));
                if (text.Length > 0) lines.Add(new TranscriptLine(time, role, text));
            }
            return lines;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Text for a message part: model/tool text verbatim, tools as one-line markers.</summary>
    private static string? PartText(JsonElement part, string role)
    {
        if (!part.TryGetProperty("type", out var type)) return null;
        return type.GetString() switch
        {
            "text" => part.GetProperty("text").GetString(),
            "tool" => "· tool " + (part.GetProperty("tool").GetString() ?? "?"),
            "step-start" => null,
            "step-finish" => null,
            _ => null,
        };
    }
}
