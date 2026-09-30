namespace AgentFactory.Projects;

/// <summary>
/// A project the factory is serving. Credentials are referenced by environment
/// variable name and never read here: a project record holds names, never values.
/// </summary>
public sealed record Project(
    string Name,
    string RepoUrl,
    string WorkerImage,
    string LlmModel,
    string GitHubKeyName,
    string LlmKeyName,
    string SourceFile);
