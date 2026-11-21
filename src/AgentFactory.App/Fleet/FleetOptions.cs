namespace AgentFactory.App.Fleet;

/// <summary>
/// Engine configuration, bound from the "Fleet" section of appsettings.json.
/// </summary>
public sealed class FleetOptions
{
    public const string SectionName = "Fleet";

    /// <summary>The repo seeded as the first fleet target on a fresh store
    /// (v1's playground). Target identity itself lives in the config store;
    /// this only bootstraps it (ticket 017).</summary>
    public string PlaygroundRepo { get; set; } = "DurgaPrasadReddyV/demofactory-playground";

    /// <summary>Base branch runs are cut from.</summary>
    public string BaseBranch { get; set; } = "main";

    /// <summary>Root for per-repo clones: &lt;root&gt;/&lt;owner&gt;__&lt;name&gt;;
    /// worktrees are cut from each repo's clone (ticket 017).</summary>
    public string CloneRoot { get; set; } = "";

    /// <summary>Directory per-run worktrees are created under.</summary>
    public string WorktreeRoot { get; set; } = "";

    /// <summary>Directory for runs.json and per-run logs.</summary>
    public string DataDir { get; set; } = "data";

    /// <summary>Issue polling interval.</summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>TTL of an enrolled org's repo-list snapshot before a re-list
    /// (ticket 018; ordering isn't contractual, so detection is a name-diff).</summary>
    public int OrgRescanMinutes { get; set; } = 15;

    /// <summary>Per-target cap on not-yet-started runs an <c>all</c>-intake
    /// enrollment may hold queued at once, filled oldest-first (ADR 0003).</summary>
    public int AllIntakeBacklogCap { get; set; } = 20;

    /// <summary>Fixed port for the host-owned opencode server (never 0: random).</summary>
    public int ServerPort { get; set; } = 48180;

    /// <summary>Cap on a single agent session before it is aborted.</summary>
    public int SessionTimeoutMinutes { get; set; } = 30;

    /// <summary>Bound on one model-health probe (ticket 044): how long a
    /// "reply with ok" session may take before the model is judged dead.
    /// Generous on purpose — a healthy model answers in well under a minute;
    /// the 041 free-model hang never answered at all.</summary>
    public int ProbeTimeoutSeconds { get; set; } = 90;

    /// <summary>Max concurrent runs. 1 = serialized (the MVP posture).</summary>
    public int MaxConcurrentRuns { get; set; } = 1;

    /// <summary>Issue label that opts an issue into the fleet.</summary>
    public string OptInLabel { get; set; } = "agent-factory";
}
