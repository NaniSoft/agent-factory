namespace AgentFactory.Credentials;

/// <summary>
/// The host's environment, read. The real implementation, and the only place in the
/// factory where an environment variable's <em>value</em> is picked up: a project file
/// names a variable, and a name is committable in a way a secret is not (story 3), so
/// something has to turn the name into a value at the moment a round starts.
/// </summary>
public sealed class ProcessEnvironment : ICredentialReader
{
    public static readonly ProcessEnvironment The = new();

    public string? Read(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : Environment.GetEnvironmentVariable(name);
}
