namespace AgentFactory.Tests.Projects;

using AgentFactory.Tests.Boundary;

/// <summary>
/// The project file schema, exercised through the running factory. A project file
/// that fails validation keeps its own project out of the rotation and is reported;
/// it never stops the factory from starting.
/// </summary>
public class ProjectFileTests
{
    [Fact]
    public async Task A_project_file_in_factories_is_served()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        await using var host = await FactoryHost.StartAsync(root);

        var board = await Board.ReadAsync(host.Board);

        Assert.Equal("https://github.com/NaniSoft/nexus", board.Rendered("data-served-project", "nexus", "data-repo-url"));
        Assert.Equal("ghcr.io/nanisoft/agent-factory-worker:1", board.Rendered("data-served-project", "nexus", "data-worker-image"));
        Assert.Equal(ProjectFile.Model, board.Rendered("data-served-project", "nexus", "data-llm-model"));
    }

    [Fact]
    public async Task The_served_set_is_exactly_the_files_in_factories()
    {
        using var root = FactoryRoot.Create()
            .WithProjectFile("nexus.yaml", ProjectFile.Valid)
            .WithProjectFile("agent-factory.yaml", ProjectFile.Valid.Replace("name: nexus", "name: agent-factory"));
        await using var host = await FactoryHost.StartAsync(root);

        var board = await Board.ReadAsync(host.Board);

        Assert.Equal(["agent-factory", "nexus"], board.ValuesOf("data-served-project"));
        Assert.Equal(
            ["agent-factory.yaml", "nexus.yaml"],
            host.Projects.Projects.Select(project => project.SourceFile));
    }

    [Fact]
    public async Task An_empty_factories_directory_still_starts_the_factory()
    {
        using var root = FactoryRoot.Create();
        await using var host = await FactoryHost.StartAsync(root);

        var board = await Board.ReadAsync(host.Board);

        Assert.Empty(board.ValuesOf("data-served-project"));
    }

    [Theory]
    [MemberData(nameof(RefusedProjectFiles))]
    public async Task A_project_file_that_fails_validation_keeps_its_own_project_out_of_the_rotation(string yaml, string reason)
    {
        using var root = FactoryRoot.Create()
            .WithProjectFile("nexus.yaml", yaml)
            .WithProjectFile("agent-factory.yaml", ProjectFile.Valid.Replace("name: nexus", "name: agent-factory"));
        await using var host = await FactoryHost.StartAsync(root);

        var board = await Board.ReadAsync(host.Board);

        // The factory still starts, and the other project is unaffected.
        Assert.Equal(["agent-factory"], board.ValuesOf("data-served-project"));
        Assert.Equal(reason, board.Rendered("data-refused-file", "nexus.yaml", "data-reason"));
    }

    public static TheoryData<string, string> RefusedProjectFiles() => new()
    {
        // Partial: a required field is not there.
        { "name: nexus\n", "Partial" },
        { """
          name: nexus
          repo:
            url: https://github.com/NaniSoft/nexus
          worker:
            image: ghcr.io/nanisoft/agent-factory-worker:1
          llm:
            model: anthropic/claude-sonnet-4-5
          keys:
            github: NEXUS_GITHUB_TOKEN
          """, "Partial" },
        { """
          name: nexus
          repo:
            url: https://github.com/NaniSoft/nexus
          worker:
            image: ghcr.io/nanisoft/agent-factory-worker:1
          llm:
            model:
          keys:
            github: NEXUS_GITHUB_TOKEN
            llm: NEXUS_ANTHROPIC_API_KEY
          """, "Partial" },

        // Invalid: a field beyond the schema, or a value that is not one.
        { ProjectFile.Valid + "\nrounds: 5\n", "Invalid" },
        { ProjectFile.Valid + "\nlabel: Nexus\n", "Invalid" },
        { """
          name: nexus
          repo:
            url: https://github.com/NaniSoft/nexus
            branch: main
          worker:
            image: ghcr.io/nanisoft/agent-factory-worker:1
          llm:
            model: anthropic/claude-sonnet-4-5
          keys:
            github: NEXUS_GITHUB_TOKEN
            llm: NEXUS_ANTHROPIC_API_KEY
          """, "Invalid" },
        { ProjectFile.Valid.Replace("https://github.com/NaniSoft/nexus", "NaniSoft/nexus"), "Invalid" },
        { ProjectFile.Valid.Replace("NEXUS_GITHUB_TOKEN", "nexus github token"), "Invalid" },

        // The model is a full reference, and the factory has to know the provider it
        // names: a bare model name has no provider to derive the credential's canonical
        // name from, and an unknown provider would hand the agent a key under a name
        // nothing reads — the silent failure the model-reference decision (#38) refuses.
        { ProjectFile.Valid.Replace("model: anthropic/claude-sonnet-4-5", "model: claude-sonnet-4-5"), "Invalid" },
        { ProjectFile.Valid.Replace("model: anthropic/claude-sonnet-4-5", "model: alien-ai/x1"), "Invalid" },
        { ProjectFile.Valid.Replace("model: anthropic/claude-sonnet-4-5", "provider: anthropic"), "Invalid" },
    };

    [Fact]
    public async Task A_model_on_opencodes_own_gateway_is_a_known_provider()
    {
        // The reviewer builds with an OpenCode gateway key, so the project names
        // opencode/<model> — the provider the CLI itself serves, whose credential the
        // factory must re-emit under the name that gateway reads.
        using var root = FactoryRoot.Create().WithProjectFile(
            "hello.yaml",
            ProjectFile.Valid.Replace("name: nexus", "name: hello")
                .Replace("model: anthropic/claude-sonnet-4-5", "model: opencode/claude-sonnet-4-6")
                .Replace("NEXUS_ANTHROPIC_API_KEY", "HELLO_OPENCODE_API_KEY"));
        await using var host = await FactoryHost.StartAsync(root);

        var board = await Board.ReadAsync(host.Board);

        // Served, not refused — and the credential name the project declared is the one
        // the record carries, exactly as declared.
        Assert.Equal("hello", board.ValuesOf("data-served-project").Single());
        Assert.Equal(
            "HELLO_OPENCODE_API_KEY",
            board.Rendered("data-served-project", "hello", "data-llm-key"));
        Assert.Empty(await RefusedReasons(host));
    }

    private static async Task<string> RefusedReasons(FactoryHost host)
    {
        var board = await Board.ReadAsync(host.Board);
        return board.Html.Contains("data-refused-file", StringComparison.Ordinal) ? "refused" : string.Empty;
    }

    [Fact]
    public async Task A_refused_project_file_is_reported()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", "name: nexus\n");
        await using var host = await FactoryHost.StartAsync(root);

        var board = await Board.ReadAsync(host.Board);

        Assert.True(board.Renders("nexus.yaml"));
    }

    [Fact]
    public async Task A_project_name_shared_between_two_files_is_refused()
    {
        using var root = FactoryRoot.Create()
            .WithProjectFile("nexus.yaml", ProjectFile.Valid)
            .WithProjectFile("nexus-copy.yaml", ProjectFile.Valid);
        await using var host = await FactoryHost.StartAsync(root);

        var board = await Board.ReadAsync(host.Board);

        Assert.Empty(board.ValuesOf("data-served-project"));
        Assert.Equal("Shared", board.Rendered("data-refused-file", "nexus.yaml", "data-reason"));
        Assert.Equal("Shared", board.Rendered("data-refused-file", "nexus-copy.yaml", "data-reason"));
    }

    [Fact]
    public async Task A_project_file_that_includes_another_file_is_refused()
    {
        using var root = FactoryRoot.Create()
            .WithProjectFile("nexus.yaml", "include: shared.yaml\n" + ProjectFile.Valid);
        await using var host = await FactoryHost.StartAsync(root);

        var board = await Board.ReadAsync(host.Board);

        Assert.Empty(board.ValuesOf("data-served-project"));
        Assert.Equal("Included", board.Rendered("data-refused-file", "nexus.yaml", "data-reason"));
    }

    [Fact]
    public async Task A_project_file_with_a_shared_field_is_refused()
    {
        using var root = FactoryRoot.Create()
            .WithProjectFile("shared.yaml", """
                name: shared
                repo:
                  url: https://github.com/NaniSoft/nexus
                worker:
                  image: ghcr.io/nanisoft/agent-factory-worker:1
                llm:
                  model: anthropic/claude-sonnet-4-5
                keys:
                  github: NEXUS_GITHUB_TOKEN
                  llm: NEXUS_ANTHROPIC_API_KEY
                """)
            .WithProjectFile("nexus.yaml", """
                name: nexus
                repo:
                  url: https://github.com/NaniSoft/nexus
                worker:
                  <<: &shared
                    image: ghcr.io/nanisoft/agent-factory-worker:1
                  image: ghcr.io/nanisoft/agent-factory-worker:1
                llm:
                  model: anthropic/claude-sonnet-4-5
                keys:
                  github: NEXUS_GITHUB_TOKEN
                  llm: NEXUS_ANTHROPIC_API_KEY
                """);
        await using var host = await FactoryHost.StartAsync(root);

        var board = await Board.ReadAsync(host.Board);

        Assert.Equal(["shared"], board.ValuesOf("data-served-project"));
        Assert.Equal("Shared", board.Rendered("data-refused-file", "nexus.yaml", "data-reason"));
    }

    [Fact]
    public async Task A_generated_project_file_is_refused()
    {
        using var root = FactoryRoot.Create()
            .WithProjectFile("nexus.generated.yaml", ProjectFile.Valid);
        await using var host = await FactoryHost.StartAsync(root);

        var board = await Board.ReadAsync(host.Board);

        Assert.Empty(board.ValuesOf("data-served-project"));
        Assert.Equal("Generated", board.Rendered("data-refused-file", "nexus.generated.yaml", "data-reason"));
    }

    [Fact]
    public async Task Credentials_are_referenced_by_environment_variable_name_and_never_read()
    {
        const string secret = "ghp-a-value-that-must-never-be-rendered";
        Environment.SetEnvironmentVariable("NEXUS_GITHUB_TOKEN", secret);
        Environment.SetEnvironmentVariable("NEXUS_ANTHROPIC_API_KEY", secret);

        try
        {
            using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
            await using var host = await FactoryHost.StartAsync(root);

            var board = await Board.ReadAsync(host.Board);

            Assert.Equal("NEXUS_GITHUB_TOKEN", board.Rendered("data-served-project", "nexus", "data-github-key"));
            Assert.Equal("NEXUS_ANTHROPIC_API_KEY", board.Rendered("data-served-project", "nexus", "data-llm-key"));
            Assert.DoesNotContain(secret, board.Html, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NEXUS_GITHUB_TOKEN", null);
            Environment.SetEnvironmentVariable("NEXUS_ANTHROPIC_API_KEY", null);
        }
    }
}
