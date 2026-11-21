using System.Text.RegularExpressions;
using AgentFactory.App.Fleet;

namespace AgentFactory.App.Api;

/// <summary>
/// The save-time rules for the two writable configuration surfaces. The app
/// validates before it posts so a reader sees the refusal where they typed it;
/// this is the authoritative copy, because a second writer to the same JSON
/// (curl, a hand edit, a future client) must meet the same rules.
/// </summary>
public static class PipelineRules
{
    /// <summary>The six placeholders a phase template may render.</summary>
    public static IReadOnlyList<string> PlaceholderNames => Placeholders.All;

    /// <summary>
    /// A JSON body can carry null for any of these because it came from a
    /// hand-written file as easily as from the app. Normalise once here, before
    /// validation, so every rule below reads a shape it can trust.
    /// </summary>
    public static void Normalize(PipelineDefinition pipeline)
    {
        pipeline.Name ??= string.Empty;
        pipeline.Phases ??= [];
        foreach (var phase in pipeline.Phases)
        {
            phase.Name ??= string.Empty;
            phase.Skills ??= [];
            phase.Template ??= string.Empty;
            phase.ProfileId = string.IsNullOrWhiteSpace(phase.ProfileId) ? null : phase.ProfileId;
            phase.ModelId = string.IsNullOrWhiteSpace(phase.ModelId) ? null : phase.ModelId;
        }
    }

    public static IReadOnlyList<string> Validate(
        PipelineDefinition pipeline,
        IReadOnlyList<AgentProfile> profiles,
        IReadOnlyList<string> skills)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(pipeline.Name))
        {
            errors.Add("pipeline: name is empty");
        }
        if (pipeline.Phases.Count == 0)
        {
            errors.Add("pipeline: at least one phase is required");
        }

        foreach (var (phase, index) in pipeline.Phases.Select((phase, index) => (phase, index)))
        {
            var label = $"phase {index + 1} '{phase.Name}'";
            if (string.IsNullOrWhiteSpace(phase.Name))
            {
                errors.Add($"{label}: name is empty");
            }
            if (string.IsNullOrWhiteSpace(phase.Template))
            {
                errors.Add($"{label}: template is empty");
            }

            foreach (var placeholder in Placeholders.Unknown(phase.Template))
            {
                errors.Add($"{label}: unknown placeholder {{{{{placeholder}}}}} — allowed: " +
                    string.Join(", ", Placeholders.All.Select(p => $"{{{{{p}}}}}")));
            }

            foreach (var unknown in phase.Skills.Where(s => !skills.Contains(s, StringComparer.Ordinal)))
            {
                errors.Add($"{label}: unknown skill '{unknown}' — not in the host-side skill library");
            }

            if (phase.ProfileId is { Length: > 0 } pinId)
            {
                var profile = profiles.FirstOrDefault(p => p.Id == pinId);
                if (profile is null)
                {
                    errors.Add($"{label}: dangling pin — profile '{pinId}' no longer exists; re-pin or clear");
                }
                else if (phase.ModelId is not { Length: > 0 })
                {
                    errors.Add($"{label}: pin is atomic — pick a model from '{profile.Name}'s palette, or clear the profile");
                }
                else if (!profile.Models.Contains(phase.ModelId, StringComparer.Ordinal))
                {
                    errors.Add($"{label}: pinned model '{phase.ModelId}' is not in '{profile.Name}'s palette");
                }
            }
            else if (phase.ModelId is { Length: > 0 })
            {
                errors.Add($"{label}: model pinned without a profile — pins are atomic");
            }
        }

        return errors;
    }

    /// <summary>Give a new pipeline an id, and leave an existing one alone.</summary>
    public static void EnsureId(PipelineDefinition pipeline, IReadOnlyList<PipelineDefinition> existing)
    {
        if (pipeline.Id is { Length: > 0 })
        {
            return;
        }
        pipeline.Id = UniqueId(Slugify(pipeline.Name), existing.Select(p => p.Id));
    }

    public static string Slugify(string name)
    {
        var slug = string.Concat(name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        slug = Regex.Replace(slug, "-+", "-").Trim('-');
        return slug.Length == 0 ? "pipeline" : slug;
    }

    public static string UniqueId(string baseId, IEnumerable<string> existing)
    {
        var taken = existing.ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(baseId))
        {
            return baseId;
        }
        var n = 2;
        while (taken.Contains($"{baseId}-{n}"))
        {
            n++;
        }
        return $"{baseId}-{n}";
    }

    /// <summary>Profile ids follow the same shape, and a rename never changes one.</summary>
    public static string ProfileId(string name, IEnumerable<string> existing) =>
        UniqueId(Slugify(name), existing);

    /// <summary>The sample issue the pipeline preview renders against.</summary>
    public static IReadOnlyDictionary<string, string> SampleIssue { get; } = new Dictionary<string, string>
    {
        [Placeholders.IssueTitle] = "Add retry backoff to the GitHub poller",
        [Placeholders.IssueBody] =
            "The poller hammers the API when GitHub is degraded.\n\n" +
            "Add exponential backoff (1s to 30s cap) and log the current backoff at debug.",
        [Placeholders.Repo] = "DurgaPrasadReddyV/demofactory-playground",
        [Placeholders.Branch] = "agent/issue-12-1",
        [Placeholders.Base] = "main",
        [Placeholders.DefinitionOfDone] = "dotnet build && dotnet test",
    };
}
