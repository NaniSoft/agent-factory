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
}
