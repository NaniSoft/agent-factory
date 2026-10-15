namespace AgentFactory.Application;

/// <summary>
/// The factory's own settings: where the project files live, where the store lives,
/// where the board is published, and whether silence may merge. None of it is per
/// project — project files are the only per-project configuration, and there is no other.
/// </summary>
public sealed record FactoryOptions(
    string FactoriesDirectory,
    string DatabasePath,
    Uri BoardUrl,
    bool AutoMerge = false)
{
    /// <summary>
    /// The board is published on port 5000 and bound to loopback. Loopback is a named
    /// compensating control, not a default: the machine is the trust boundary.
    /// </summary>
    public static readonly Uri DefaultBoardUrl = new("http://127.0.0.1:5000");

    /// <summary>
    /// Where a round's lifted-out result file and working tree land, one directory per
    /// round. Beside the store rather than in a temporary directory, and kept after the
    /// round, because the host has to be able to reach the round's commit once the
    /// container that made it is gone: the host pushes, not the container (ADR-0006).
    /// </summary>
    public string RoundsDirectory => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(DatabasePath)) ?? "data",
        "rounds");

    /// <summary>
    /// Where credential values live when they are not in the environment: one file per
    /// credential, the name is the file (#32). Beside the project files, because the two
    /// are written by the same surface — the board's projects page — and mounted from the
    /// same kind of volume on a deployment. A name, never a value, travels anywhere else
    /// in the process.
    /// </summary>
    public string SecretsDirectory => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(FactoriesDirectory)) ?? "factories",
        "secrets");

    /// <summary>
    /// The Temporal endpoint the per-ticket workflows run against, or null for store-only
    /// mode. Configured, the ticket's story is a durable workflow: a round in flight
    /// survives a restart of this process, and the board's clicks reach the workflow as
    /// updates. Null, the factory runs exactly as it always did — the loop steps tickets
    /// from the store, and a restart mid-round strands the round, which is the gap the
    /// workflow exists to close.
    /// </summary>
    public string? TemporalEndpoint { get; init; }
}
