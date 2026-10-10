namespace AgentFactory.Credentials;

using AgentFactory.Projects;

/// <summary>
/// The credential names the factory can use, and the one way a credential's file is
/// removed. Names travel everywhere; values do not.
/// </summary>
/// <remarks>
/// A credential's name reaches the factory two ways with no registry between them, the
/// way a project's does: a served project declares the name its GitHub and LLM keys live
/// under, and a file in the secrets directory <em>is</em> a value under that name, written
/// by the board's form (#32). The union is the whole of what the surface can offer to set
/// or remove, and looking at either source alone silently hides half of it — a declared
/// name with no value yet, or a value nobody is currently routing.
/// </remarks>
public static class CredentialFiles
{
    /// <summary>
    /// The credential names: the union of the keys the served projects declare and the
    /// files already present in the secrets directory, in a stable order.
    /// </summary>
    public static IReadOnlyList<string> Names(ProjectLoadReport projects, string secretsDirectory)
    {
        ArgumentNullException.ThrowIfNull(projects);

        return projects.Projects
            .SelectMany(project => new[] { project.GitHubKeyName, project.LlmKeyName })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Concat(OnDisk(secretsDirectory))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The credential names present as files in the secrets directory, one file per name.</summary>
    public static IReadOnlyList<string> OnDisk(string secretsDirectory)
    {
        if (string.IsNullOrWhiteSpace(secretsDirectory) || !Directory.Exists(secretsDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(secretsDirectory)
            .Select(path => Path.GetFileName(path) ?? string.Empty)
            .Where(name => name.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Whether a name can be a file in the secrets directory. A name arrives from a
    /// route segment, so it is kept to a single path component of letters, digits,
    /// underscores, hyphens and dots — enough for any environment-variable name and
    /// nothing that could climb out of the directory.
    /// </summary>
    public static bool IsServableName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name == Path.GetFileName(name)
        && name is not "." and not ".."
        && name.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.');

    /// <summary>Removes one credential's file, if it is one.</summary>
    public static void Delete(string secretsDirectory, string name)
    {
        if (!IsServableName(name))
        {
            return;
        }

        var path = Path.Combine(secretsDirectory, name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
