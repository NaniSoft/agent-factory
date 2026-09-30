namespace AgentFactory.Projects;

/// <summary>
/// Writes and removes project files in <c>factories/</c> on the board's behalf. The file
/// stays the single source of truth: the page is an editor of exactly the schema the
/// loader accepts, and nothing else — no field beyond the six, no partial file, no
/// <c>*.generated.yaml</c>, which the loader refuses on sight.
/// </summary>
public static class ProjectFiles
{
    /// <summary>The six values a project file states, in the order the schema fixes them.</summary>
    public static string Text(
        string name,
        string repoUrl,
        string workerImage,
        string llmModel,
        string githubKeyName,
        string llmKeyName) => $"""
        # Written by the board's projects page. A project file states every value itself:
        # credentials are referenced by name and are never read into this file.
        name: {name}
        repo:
          url: {repoUrl}
        worker:
          image: {workerImage}
        llm:
          model: {llmModel}
        keys:
          github: {githubKeyName}
          llm: {llmKeyName}
        """;

    /// <summary>Whether a project name can be a file name the loader would serve.</summary>
    public static bool IsServableName(string name) =>
        name.Length > 0
        && name.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_')
        && !name.EndsWith(".generated.yaml", StringComparison.OrdinalIgnoreCase)
        && !name.EndsWith(".generated.yml", StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes one project file, or says why it would not.</summary>
    public static string? Write(
        string factoriesDirectory,
        string name,
        string repoUrl,
        string workerImage,
        string llmModel,
        string githubKeyName,
        string llmKeyName)
    {
        if (!IsServableName(name))
        {
            return "the project name would not be a file the loader serves";
        }

        Directory.CreateDirectory(factoriesDirectory);
        File.WriteAllText(
            Path.Combine(factoriesDirectory, $"{name}.yaml"),
            Text(name, repoUrl, workerImage, llmModel, githubKeyName, llmKeyName));
        return null;
    }

    /// <summary>Removes one project's file, if it is one.</summary>
    public static void Delete(string factoriesDirectory, string name)
    {
        var path = Path.Combine(factoriesDirectory, $"{name}.yaml");
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
