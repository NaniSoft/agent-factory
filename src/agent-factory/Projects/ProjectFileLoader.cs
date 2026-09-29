namespace AgentFactory.Projects;

using Microsoft.Extensions.Logging;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

/// <summary>
/// Reads every project file in <c>factories/</c> and exposes the ones that validate.
/// A file that fails validation takes only its own project out of the rotation and is
/// reported; it never throws into the running factory.
/// </summary>
/// <remarks>
/// The schema is exactly <c>name</c>, <c>repo.url</c>, <c>worker.image</c>,
/// <c>llm.provider</c>, <c>keys.github</c> and <c>keys.llm</c>. Nothing further is
/// accepted: no prompts, no per-issue overrides, no board configuration, no plugin
/// list. Credentials are referenced by environment variable name and are never read
/// here, so a project record holds names and no values.
/// </remarks>
internal static class ProjectFileLoader
{
    private static readonly string[] TopLevelFields = ["name", "repo", "worker", "llm", "keys"];

    private static readonly string[] IncludeFields = ["include", "extends", "import"];

    private static readonly string[] RepoFields = ["url"];
    private static readonly string[] WorkerFields = ["image"];
    private static readonly string[] LlmFields = ["provider"];
    private static readonly string[] KeyFields = ["github", "llm"];

    public static ProjectLoadReport Load(string factoriesDirectory, ILogger logger)
    {
        if (!Directory.Exists(factoriesDirectory))
        {
            logger.LogWarning(
                "No factories directory at {Directory}. The factory is serving no projects",
                factoriesDirectory);
            return ProjectLoadReport.Empty;
        }

        var served = new List<Project>();
        var refused = new List<ProjectFileRejection>();

        foreach (var path in ProjectFiles(factoriesDirectory))
        {
            var fileName = Path.GetFileName(path);

            try
            {
                if (IsGenerated(fileName))
                {
                    throw Refused(fileName, ProjectRejectionReason.Generated,
                        "a generated project file is derived by the loader, not written by anyone");
                }

                served.Add(Read(path, fileName));
            }
            catch (ProjectFileRefusedException refusal)
            {
                Refuse(refused, logger, refusal.Rejection);
            }
        }

        var shared = served
            .GroupBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToList();

        foreach (var project in shared)
        {
            served.Remove(project);
            Refuse(refused, logger, new ProjectFileRejection(
                project.SourceFile, ProjectRejectionReason.Shared,
                $"the project name is declared in more than one file, and a project is exactly one file ({project.Name})"));
        }

        // The rotation order comes from the directory, sorted by file name, so it is
        // reproducible without anyone maintaining an ordering.
        var report = new ProjectLoadReport(
            served.OrderBy(project => project.SourceFile, StringComparer.OrdinalIgnoreCase).ToList(),
            refused.OrderBy(rejection => rejection.FileName, StringComparer.OrdinalIgnoreCase).ToList());

        Served(logger, factoriesDirectory, report);
        return report;
    }

    /// <summary>
    /// What this process is actually serving, said at start, once per project and once in
    /// total (story 63). The board renders the same set, but the board is a surface a human
    /// has to remember to open, and the question this answers — "did my change to
    /// <c>factories/</c> take effect, and what is in the rotation" — is asked of the log
    /// first and most often.
    /// </summary>
    /// <remarks>
    /// Every field logged here is a field of <see cref="Project"/>, and that record holds
    /// credential <em>names</em> and no values at all: the two key fields are environment
    /// variable names the project file declared, and this loader never reads a value, so
    /// there is nothing here that could be one. That is the reason logging a project's whole
    /// configuration is safe here and would not be safe anywhere else in the process, and it
    /// is a property of the type rather than of this method — there is nowhere on
    /// <see cref="Project"/> for a secret to sit. The issue title is not logged and neither
    /// is a reviewer's words, because this is configuration rather than work, and neither is
    /// anything a project is going to be asked to keep in a telemetry store.
    /// </remarks>
    private static void Served(ILogger logger, string factoriesDirectory, ProjectLoadReport report)
    {
        logger.LogInformation(
            "Serving {Served} project(s) from {Directory}, in rotation order, and refused {Refused} file(s).",
            report.Projects.Count,
            factoriesDirectory,
            report.Rejections.Count);

        foreach (var project in report.Projects)
        {
            logger.LogInformation(
                "Serving project {Name} from {File}: repository {Repository}, worker image {Image}, LLM provider {Provider}, "
                    + "GitHub credential named {GitHubKey}, LLM credential named {LlmKey}. Names, not values — this loader "
                    + "never reads a credential.",
                project.Name,
                project.SourceFile,
                project.RepoUrl,
                project.WorkerImage,
                project.LlmProvider,
                project.GitHubKeyName,
                project.LlmKeyName);
        }
    }

    private static IEnumerable<string> ProjectFiles(string factoriesDirectory) =>
        Directory.EnumerateFiles(factoriesDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path) is { } extension
                && (extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);

    private static bool IsGenerated(string fileName) =>
        fileName.EndsWith(".generated.yaml", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".generated.yml", StringComparison.OrdinalIgnoreCase);

    private static Project Read(string path, string fileName)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException error)
        {
            throw Refused(fileName, ProjectRejectionReason.Invalid, $"the file could not be read ({error.Message})");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw Refused(fileName, ProjectRejectionReason.Partial,
                "the file is empty, and a project file states every value itself");
        }

        YamlStream stream;
        try
        {
            stream = new YamlStream();
            using var reader = new StringReader(text);
            stream.Load(reader);
        }
        catch (YamlException error)
        {
            // A YAML exception quotes the offending line, which is a value from the file.
            // Only the position is safe to report.
            throw Refused(fileName, ProjectRejectionReason.Invalid,
                $"the file is not valid YAML (line {error.Start.Line}, column {error.Start.Column})");
        }

        if (stream.Documents.Count > 1)
        {
            throw Refused(fileName, ProjectRejectionReason.Shared,
                "a project file is one project, and this file declares more than one");
        }

        if (stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw Refused(fileName, ProjectRejectionReason.Invalid,
                "a project file is a mapping of the five fields the factory accepts");
        }

        if (SharesAField(root))
        {
            throw Refused(fileName, ProjectRejectionReason.Shared,
                "a field is shared from another mapping, and a project file states every value itself");
        }

        var fields = Fields(root);

        foreach (var field in IncludeFields.Where(field => fields.ContainsKey(field)))
        {
            throw Refused(fileName, ProjectRejectionReason.Included,
                $"the file includes {field}, and a project file is one complete file");
        }

        foreach (var field in fields.Keys.Where(field => !TopLevelFields.Contains(field, StringComparer.Ordinal)))
        {
            throw Refused(fileName, ProjectRejectionReason.Invalid,
                $"{field} is not a field of a project file, and nothing beyond the five is accepted");
        }

        foreach (var field in TopLevelFields.Where(field => !fields.ContainsKey(field)))
        {
            throw Refused(fileName, ProjectRejectionReason.Partial,
                $"{field} is missing, and a project file states every value itself");
        }

        var repo = Group(fileName, "repo", RepoFields, fields["repo"]);
        var worker = Group(fileName, "worker", WorkerFields, fields["worker"]);
        var llm = Group(fileName, "llm", LlmFields, fields["llm"]);
        var keys = Group(fileName, "keys", KeyFields, fields["keys"]);

        var name = Scalar(fileName, "name", fields["name"]);
        var repoUrl = Scalar(fileName, "repo.url", repo);
        var workerImage = Scalar(fileName, "worker.image", worker);
        var llmProvider = Scalar(fileName, "llm.provider", llm);
        var githubKey = Scalar(fileName, "keys.github", keys);
        var llmKey = Scalar(fileName, "keys.llm", keys);

        if (!Uri.TryCreate(repoUrl, UriKind.Absolute, out var repository)
            || (repository.Scheme != Uri.UriSchemeHttp && repository.Scheme != Uri.UriSchemeHttps))
        {
            throw Refused(fileName, ProjectRejectionReason.Invalid,
                "repo.url is not an absolute http or https URL");
        }

        if (!IsEnvironmentVariableName(githubKey))
        {
            throw Refused(fileName, ProjectRejectionReason.Invalid,
                "keys.github is not the name of an environment variable, and credentials are referenced by name");
        }

        if (!IsEnvironmentVariableName(llmKey))
        {
            throw Refused(fileName, ProjectRejectionReason.Invalid,
                "keys.llm is not the name of an environment variable, and credentials are referenced by name");
        }

        return new Project(name, repoUrl, workerImage, llmProvider, githubKey, llmKey, fileName);
    }

    private static YamlMappingNode Group(string fileName, string field, string[] allowed, YamlNode node)
    {
        if (node is not YamlMappingNode mapping)
        {
            throw Refused(fileName, ProjectRejectionReason.Partial,
                $"{field} is missing, and a project file states every value itself");
        }

        var leaves = Fields(mapping);

        foreach (var leaf in leaves.Keys.Where(leaf => !allowed.Contains(leaf, StringComparer.Ordinal)))
        {
            throw Refused(fileName, ProjectRejectionReason.Invalid,
                $"{field}.{leaf} is not a field of a project file, and nothing beyond the five is accepted");
        }

        foreach (var leaf in allowed.Where(leaf => !leaves.ContainsKey(leaf)))
        {
            throw Refused(fileName, ProjectRejectionReason.Partial,
                $"{field}.{leaf} is missing, and a project file states every value itself");
        }

        return mapping;
    }

    private static string Scalar(string fileName, string field, YamlNode? node)
    {
        var value = field.Contains('.') && node is YamlMappingNode mapping
            ? mapping.Children.FirstOrDefault(pair => KeyOf(pair.Key) == field[(field.IndexOf('.') + 1)..]).Value
            : node;

        if (value is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
        {
            throw Refused(fileName, ProjectRejectionReason.Partial,
                $"{field} is missing or empty, and a project file states every value itself");
        }

        return scalar.Value;
    }

    private static Dictionary<string, YamlNode> Fields(YamlMappingNode mapping) =>
        mapping.Children
            .GroupBy(pair => KeyOf(pair.Key), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);

    private static bool SharesAField(YamlNode node) =>
        node is YamlMappingNode mapping
        && (mapping.Children.Any(pair => KeyOf(pair.Key) == "<<")
            || mapping.Children.Any(pair => pair.Value is YamlMappingNode child && SharesAField(child)));

    private static string KeyOf(YamlNode node) =>
        node is YamlScalarNode scalar ? scalar.Value ?? string.Empty : string.Empty;

    private static bool IsEnvironmentVariableName(string value) =>
        value.Length > 0
        && (char.IsAsciiLetter(value[0]) || value[0] == '_')
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static ProjectFileRefusedException Refused(
        string fileName, ProjectRejectionReason reason, string message) =>
        new(new ProjectFileRejection(fileName, reason, message));

    private static void Refuse(List<ProjectFileRejection> refused, ILogger logger, ProjectFileRejection rejection)
    {
        refused.Add(rejection);
        logger.LogWarning(
            "Refused project file {File}: {Reason} — {Message}. Its project is out of the rotation",
            rejection.FileName, rejection.Reason, rejection.Message);
    }

    private sealed class ProjectFileRefusedException(ProjectFileRejection rejection) : Exception
    {
        public ProjectFileRejection Rejection { get; } = rejection;
    }
}
