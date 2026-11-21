namespace AgentFactory.App.Fleet;

/// <summary>
/// Intake rule names (ticket 013): how an issue on a fleet target becomes
/// fleet work. <c>all</c> (open backlog, oldest-first) is exercised with the
/// enrollment scan — ticket 018.
/// </summary>
public static class IntakeRules
{
    public const string Label = "label";
    public const string All = "all";
}

/// <summary>
/// One enrolled fleet target (ticket 017): a repo's identity plus the
/// per-target settings the engine executes with — intake rule, pipeline
/// assignment, enabled flag, and the definition-of-done command string that
/// renders into phase prompts. Lives in the config store next to pipelines.
/// </summary>
public sealed class TargetRecord
{
    /// <summary>Repo identity, owner/name.</summary>
    public string Repo { get; set; } = "";

    /// <summary>Set when the target was enrolled via an org scan (ticket 018);
    /// null for individually enrolled repos.</summary>
    public string? Org { get; set; }

    /// <summary>Intake rule — <see cref="IntakeRules.Label"/> or
    /// <see cref="IntakeRules.All"/>.</summary>
    public string Intake { get; set; } = IntakeRules.Label;

    /// <summary>Explicit pipeline assignment; null walks the fleet default
    /// (most-specific-wins, ticket 013).</summary>
    public string? PipelineId { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Per-target definition-of-done commands rendered into phase
    /// prompts via {{definition_of_done}} — one pipeline serves any stack.</summary>
    public string DefinitionOfDone { get; set; } = "dotnet build && dotnet test";

    public TargetRecord Clone() => new()
    {
        Repo = Repo,
        Org = Org,
        Intake = Intake,
        PipelineId = PipelineId,
        Enabled = Enabled,
        DefinitionOfDone = DefinitionOfDone,
    };
}

/// <summary>
/// An enrolled org (ticket 018): the template the org scan materializes into a
/// <see cref="TargetRecord"/> per discovered repo. Settings copy at discovery
/// time — later org edits reach newly discovered repos only; per-target tuning
/// edits the target (the 022 UI surface). A disabled org stops scanning (no
/// discoveries, no removals); its existing targets keep their own flags.
/// </summary>
public sealed class OrgRecord
{
    /// <summary>Org login, e.g. <c>NaniSoft</c>.</summary>
    public string Org { get; set; } = "";

    public string Intake { get; set; } = IntakeRules.Label;

    /// <summary>Explicit pipeline assignment materialized into discovered
    /// targets; null walks the fleet default.</summary>
    public string? PipelineId { get; set; }

    public bool Enabled { get; set; } = true;

    public string DefinitionOfDone { get; set; } = "dotnet build && dotnet test";
}
