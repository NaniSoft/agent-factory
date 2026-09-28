namespace AgentFactory.Tests.Boundary;

/// <summary>The one shape a project file is allowed to have, written out by a test.</summary>
public static class ProjectFile
{
    public const string Valid = """
        name: nexus
        repo:
          url: https://github.com/NaniSoft/nexus
        worker:
          image: ghcr.io/nanisoft/agent-factory-worker:1
        llm:
          provider: anthropic
        keys:
          github: NEXUS_GITHUB_TOKEN
          llm: NEXUS_ANTHROPIC_API_KEY
        """;

    /// <summary>
    /// The same six fields for a project of the test's own naming. Credential names are
    /// derived from the project's name so two projects in one directory never share a
    /// key, which is the shape a real rotation has.
    /// </summary>
    public static string For(string name, string repoUrl) => $$"""
        name: {{name}}
        repo:
          url: {{repoUrl}}
        worker:
          image: ghcr.io/nanisoft/agent-factory-worker:1
        llm:
          provider: anthropic
        keys:
          github: {{name.ToUpperInvariant()}}_GITHUB_TOKEN
          llm: {{name.ToUpperInvariant()}}_ANTHROPIC_API_KEY
        """;
}
