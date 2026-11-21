using System.Text.RegularExpressions;

namespace AgentFactory.App.Fleet;

/// <summary>
/// One step of a pipeline definition: a single engine session's prompt template
/// and skill set (ticket 011), plus its optional agent-profile pin (ticket
/// 030). Config-shaped — lives in the config store and is edited in the UI
/// (ticket 021).
/// </summary>
public sealed class PhaseDefinition
{
    public string Name { get; set; } = "";
    public List<string> Skills { get; set; } = [];
    public string Template { get; set; } = "";

    /// <summary>The agent profile this phase pins (ticket 030), or null to run
    /// on engine defaults exactly as before profiles existed. Pins are atomic
    /// (ticket 028): profile and model together, or neither — validated at run
    /// start and at save.</summary>
    public string? ProfileId { get; set; }
    public string? ModelId { get; set; }
}

/// <summary>
/// A named, ordered pipeline of phases. The fleet default is walked by targets
/// without an explicit assignment (ticket 013).
/// </summary>
public sealed class PipelineDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<PhaseDefinition> Phases { get; set; } = [];

    public PipelineDefinition Clone() => new()
    {
        Id = Id,
        Name = Name,
        Phases = [.. Phases.Select(p => new PhaseDefinition
        {
            Name = p.Name,
            Skills = [.. p.Skills],
            Template = p.Template,
            ProfileId = p.ProfileId,
            ModelId = p.ModelId,
        })],
    };
}

/// <summary>
/// The values a phase template renders with — exactly the six placeholders
/// guaranteed by ticket 011; anything else in a template is a configuration
/// error caught at run start.
/// </summary>
public sealed record PromptVars(
    string IssueTitle,
    string IssueBody,
    string Repo,
    string Branch,
    string Base,
    string DefinitionOfDone);

public static class Placeholders
{
    public const string IssueTitle = "issue_title";
    public const string IssueBody = "issue_body";
    public const string Repo = "repo";
    public const string Branch = "branch";
    public const string Base = "base";
    public const string DefinitionOfDone = "definition_of_done";

    public static readonly string[] All =
        [IssueTitle, IssueBody, Repo, Branch, Base, DefinitionOfDone];

    /// <summary>Render the six known placeholders; unknown ones stay verbatim
    /// (and are caught by <see cref="Unknown"/> at run start).</summary>
    public static string Render(string template, PromptVars v) => template
        .Replace("{{" + IssueTitle + "}}", v.IssueTitle)
        .Replace("{{" + IssueBody + "}}", v.IssueBody)
        .Replace("{{" + Repo + "}}", v.Repo)
        .Replace("{{" + Branch + "}}", v.Branch)
        .Replace("{{" + Base + "}}", v.Base)
        .Replace("{{" + DefinitionOfDone + "}}", v.DefinitionOfDone);

    /// <summary>Placeholder names present in the template but not guaranteed.</summary>
    public static List<string> Unknown(string template) =>
        [.. Regex.Matches(template, @"\{\{(\w+)\}\}")
            .Select(m => m.Groups[1].Value)
            .Where(name => !All.Contains(name))
            .Distinct()];
}
