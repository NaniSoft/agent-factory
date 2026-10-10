namespace AgentFactory.Tests.Api;

using System.Net;
using System.Text;
using System.Text.Json;
using AgentFactory.Tests.Boundary;

/// <summary>
/// The Projects write and read surface, driven over the process's real HTTP surface.
/// The factory, the loader and the store are the real thing; only the seams are
/// substituted, and nothing here waits. What is asserted is exactly what a renderer
/// reads and posts: the served set with its load state, the refused files, and the
/// three writes the board has always had, each inventing no capability.
/// </summary>
public class ProjectsEndpointTests
{
    private const string ValidProject =
        """
        name: nexus
        repo:
          url: https://github.com/NaniSoft/nexus
        worker:
          image: ghcr.io/nanisoft/agent-factory-worker:1
        llm:
          model: anthropic/claude-sonnet-4-5
        keys:
          github: NEXUS_GITHUB_TOKEN
          llm: NEXUS_ANTHROPIC_API_KEY
        """;

    [Fact]
    public async Task Listing_projects_reports_the_served_set_and_the_refused_files()
    {
        using var root = FactoryRoot.Create()
            .WithProjectFile("nexus.yaml", ValidProject)
            .WithRefusedFile("broken.yaml");
        await using var host = await FactoryHost.StartAsync(root);

        using var response = await host.Board.GetAsync("/api/projects");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var projects = document.RootElement.GetProperty("projects");

        // The six values the file states, and the load state the loader decided: a
        // project in this list is one the factory serves.
        var nexus = Assert.Single(projects.EnumerateArray());
        Assert.Equal("nexus", nexus.GetProperty("name").GetString());
        Assert.Equal("https://github.com/NaniSoft/nexus", nexus.GetProperty("repoUrl").GetString());
        Assert.Equal("ghcr.io/nanisoft/agent-factory-worker:1", nexus.GetProperty("workerImage").GetString());
        Assert.Equal("anthropic/claude-sonnet-4-5", nexus.GetProperty("llmModel").GetString());
        Assert.Equal("NEXUS_GITHUB_TOKEN", nexus.GetProperty("githubKeyName").GetString());
        Assert.Equal("NEXUS_ANTHROPIC_API_KEY", nexus.GetProperty("llmKeyName").GetString());
        Assert.Equal("nexus.yaml", nexus.GetProperty("sourceFile").GetString());
        Assert.Equal("served", nexus.GetProperty("state").GetString());

        // The refused file is reported rather than swallowed, with the factory's own
        // reason and message — the UI invents no validation of its own.
        var refusal = Assert.Single(document.RootElement.GetProperty("rejections").EnumerateArray());
        Assert.Equal("broken.yaml", refusal.GetProperty("fileName").GetString());
        Assert.Equal("Partial", refusal.GetProperty("reason").GetString());
        Assert.False(string.IsNullOrWhiteSpace(refusal.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task Adding_a_project_writes_the_file_the_loader_accepts()
    {
        using var root = FactoryRoot.Create();
        await using var host = await FactoryHost.StartAsync(root);

        using var response = await host.Board.PostAsync(
            "/api/projects",
            Json(
                """
                {
                  "name": "atlas",
                  "repoUrl": "https://github.com/NaniSoft/atlas",
                  "workerImage": "ghcr.io/nanisoft/agent-factory-worker:1",
                  "llmModel": "anthropic/claude-sonnet-4-5",
                  "githubKeyName": "ATLAS_GITHUB_TOKEN",
                  "llmKeyName": "ATLAS_ANTHROPIC_API_KEY"
                }
                """));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("error").ValueKind);

        // The file, not the endpoint, is the source of truth: what landed is exactly the
        // shape the loader serves.
        var written = await File.ReadAllTextAsync(Path.Combine(root.FactoriesDirectory, "atlas.yaml"));
        Assert.Contains("name: atlas", written, StringComparison.Ordinal);
        Assert.Contains("https://github.com/NaniSoft/atlas", written, StringComparison.Ordinal);
        Assert.Contains("ATLAS_ANTHROPIC_API_KEY", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editing_a_project_overwrites_its_file()
    {
        using var root = FactoryRoot.Create()
            .WithProjectFile("nexus.yaml", ValidProject);
        await using var host = await FactoryHost.StartAsync(root);

        using var response = await host.Board.PostAsync(
            "/api/projects",
            Json(
                """
                {
                  "name": "nexus",
                  "repoUrl": "https://github.com/NaniSoft/nexus",
                  "workerImage": "ghcr.io/nanisoft/agent-factory-worker:2",
                  "llmModel": "anthropic/claude-opus-4-5",
                  "githubKeyName": "NEXUS_GITHUB_TOKEN",
                  "llmKeyName": "NEXUS_ANTHROPIC_API_KEY"
                }
                """));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The file is overwritten rather than added beside: one project is one file.
        var written = await File.ReadAllTextAsync(Path.Combine(root.FactoriesDirectory, "nexus.yaml"));
        Assert.Contains("image: ghcr.io/nanisoft/agent-factory-worker:2", written, StringComparison.Ordinal);
        Assert.Contains("model: anthropic/claude-opus-4-5", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_write_the_loader_would_refuse_is_refused_and_writes_nothing()
    {
        using var root = FactoryRoot.Create();
        await using var host = await FactoryHost.StartAsync(root);

        // A name that would not be a file the loader serves. Refused rather than
        // written, and the refusal is the factory's own words.
        using var response = await host.Board.PostAsync(
            "/api/projects",
            Json(
                """
                {
                  "name": "bad/name",
                  "repoUrl": "https://github.com/NaniSoft/nexus",
                  "workerImage": "ghcr.io/nanisoft/agent-factory-worker:1",
                  "llmModel": "anthropic/claude-sonnet-4-5",
                  "githubKeyName": "NEXUS_GITHUB_TOKEN",
                  "llmKeyName": "NEXUS_ANTHROPIC_API_KEY"
                }
                """));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("error").GetString()));
        Assert.Empty(Directory.GetFiles(root.FactoriesDirectory, "*.yaml"));
    }

    [Fact]
    public async Task Removing_a_project_deletes_its_file()
    {
        using var root = FactoryRoot.Create()
            .WithProjectFile("nexus.yaml", ValidProject);
        await using var host = await FactoryHost.StartAsync(root);

        using var response = await host.Board.DeleteAsync("/api/projects/nexus");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(File.Exists(Path.Combine(root.FactoriesDirectory, "nexus.yaml")));
    }

    [Fact]
    public async Task Removing_an_unservable_name_is_refused_and_deletes_nothing()
    {
        using var root = FactoryRoot.Create();
        await using var host = await FactoryHost.StartAsync(root);

        // A name the loader would never serve is not something a delete may touch, and
        // the guard says so rather than deleting a path it was handed.
        using var response = await host.Board.DeleteAsync("/api/projects/not%20servable.yaml");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("error").GetString()));
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}
