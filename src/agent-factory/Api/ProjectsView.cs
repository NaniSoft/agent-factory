namespace AgentFactory.Api;

using System.Text.Json.Serialization;
using AgentFactory.Projects;

/// <summary>
/// What <c>GET /api/projects</c> returns: the set of Project files the factory serves,
/// each with the load state the loader decided for it, and the files it refused with the
/// loader's own reason and message. It is the factory's judgement serialised, so the
/// renderer never invents a second validation.
/// </summary>
/// <remarks>
/// The property names are pinned with <see cref="JsonPropertyNameAttribute"/> rather than
/// left to a naming policy, because the JSON is a contract with a separate app: a web
/// default that quietly changed casing would break the renderer without failing a build.
/// They are camelCase so the TypeScript view model and these records agree by name.
/// </remarks>
public sealed record ProjectsView(
    [property: JsonPropertyName("projects")] IReadOnlyList<ServedProjectView> Projects,
    [property: JsonPropertyName("rejections")] IReadOnlyList<ProjectFileRejectionView> Rejections)
{
    /// <summary>
    /// The served set and the refusals, mapped from the loader's one report. A project in
    /// <c>projects</c> is one the loader served, which is the only load state this surface
    /// reports for it; a file in <c>rejections</c> is one it could not.
    /// </summary>
    public static ProjectsView From(ProjectLoadReport report) => new(
        [.. report.Projects.Select(project => new ServedProjectView(
            project.Name,
            project.RepoUrl,
            project.WorkerImage,
            project.LlmModel,
            project.GitHubKeyName,
            project.LlmKeyName,
            project.SourceFile,
            "served"))],
        [.. report.Rejections.Select(rejection => new ProjectFileRejectionView(
            rejection.FileName,
            rejection.Reason.ToString(),
            rejection.Message))]);
}

/// <summary>
/// One Project the factory serves: the six values its file states, the file it was read
/// from, and the load state. Credentials are the names of environment variables and are
/// never values — the renderer shows which names a Project declares, never what they hold.
/// </summary>
public sealed record ServedProjectView(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("repoUrl")] string RepoUrl,
    [property: JsonPropertyName("workerImage")] string WorkerImage,
    [property: JsonPropertyName("llmModel")] string LlmModel,
    [property: JsonPropertyName("githubKeyName")] string GitHubKeyName,
    [property: JsonPropertyName("llmKeyName")] string LlmKeyName,
    [property: JsonPropertyName("sourceFile")] string SourceFile,
    [property: JsonPropertyName("state")] string State);

/// <summary>
/// One file in <c>factories/</c> the loader refused, with the reason it decided and the
/// message it wrote. The renderer draws these where the human acts; it adds no rule.
/// </summary>
public sealed record ProjectFileRejectionView(
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("message")] string Message);

/// <summary>The six values a write posts, exactly the shape a project file states.</summary>
public sealed record ProjectWriteRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("repoUrl")] string? RepoUrl,
    [property: JsonPropertyName("workerImage")] string? WorkerImage,
    [property: JsonPropertyName("llmModel")] string? LlmModel,
    [property: JsonPropertyName("githubKeyName")] string? GitHubKeyName,
    [property: JsonPropertyName("llmKeyName")] string? LlmKeyName);

/// <summary>
/// The outcome of a write: whether the file was written (or removed), and the factory's
/// own words for why it was not. <c>ok</c> being false is a refusal, not a failure of the
/// process, and it mirrors <c>ProjectFiles.Write</c>'s <c>string?</c>.
/// </summary>
public sealed record ProjectWriteResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string? Error);
