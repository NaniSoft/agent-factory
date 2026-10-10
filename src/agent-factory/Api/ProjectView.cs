namespace AgentFactory.Api;

using System.Text.Json.Serialization;
using AgentFactory.Projects;

/// <summary>
/// One Project the factory serves, as both the board's filter and the projects surface
/// offer it: the six values its file states, the file it was read from, and the load
/// state. Credentials are the names of environment variables and are never values — the
/// renderer shows which names a Project declares, never what they hold.
/// </summary>
/// <remarks>
/// It is one type rather than two identical ones on the two surfaces. The board's
/// <c>projects</c> and the projects endpoint's <c>projects</c> are the same project record
/// with the same names, and a second type free to drift from the first would be a second
/// source of truth about the same thing. <see cref="State"/> is the load state the loader
/// decided — <c>served</c> on the projects surface — and null on the board, which carries
/// the served set and needs no state beside it.
/// </remarks>
public sealed record ProjectView(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("repoUrl")] string RepoUrl,
    [property: JsonPropertyName("workerImage")] string WorkerImage,
    [property: JsonPropertyName("llmModel")] string LlmModel,
    [property: JsonPropertyName("githubKeyName")] string GitHubKeyName,
    [property: JsonPropertyName("llmKeyName")] string LlmKeyName,
    [property: JsonPropertyName("sourceFile")] string SourceFile,
    [property: JsonPropertyName("state")] string? State)
{
    /// <summary>One served project, with the load state a surface decided, or none.</summary>
    public static ProjectView Of(Project project, string? state = null)
    {
        ArgumentNullException.ThrowIfNull(project);

        return new ProjectView(
            project.Name,
            project.RepoUrl,
            project.WorkerImage,
            project.LlmModel,
            project.GitHubKeyName,
            project.LlmKeyName,
            project.SourceFile,
            state);
    }
}

/// <summary>
/// One file in <c>factories/</c> the loader refused, with the reason it decided and the
/// message it wrote. It reaches the board beside the projects that were served, and it
/// reaches the projects surface the same way; a refusal is reported rather than swallowed.
/// </summary>
public sealed record ProjectFileRejectionView(
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("message")] string Message)
{
    /// <summary>One refusal, read the way both surfaces read it.</summary>
    public static ProjectFileRejectionView Of(ProjectFileRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(rejection);

        return new ProjectFileRejectionView(rejection.FileName, rejection.Reason.ToString(), rejection.Message);
    }
}
