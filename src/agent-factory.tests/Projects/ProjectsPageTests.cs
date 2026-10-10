namespace AgentFactory.Tests.Projects;

using System.Net.Http.Json;
using System.Text.Json;
using AgentFactory.Credentials;
using AgentFactory.Tests.Boundary;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The projects surface, exercised through the running factory: a repository is added
/// through the endpoint the admin app posts to, a credential is written through it, and
/// what lands is the file the loader would have written — with the secret never rendered
/// anywhere.
/// </summary>
/// <remarks>
/// This drove the Razor Projects page until #51 retired it. The page's three writes are
/// now the JSON endpoints the app calls — <c>POST /api/projects</c>,
/// <c>PUT /api/credentials/{name}</c> — so this posts those and keeps asserting the two
/// things that are not the endpoint's shape: that the file is the source of truth, and
/// that the value reaches the factory's own reader and is rendered nowhere.
/// </remarks>
public class ProjectsPageTests
{
    [Fact]
    public async Task Adding_a_project_through_the_endpoint_writes_the_file_the_loader_accepts()
    {
        using var root = FactoryRoot.Create();
        await using var host = await FactoryHost.StartAsync(root);

        using (var response = await host.Board.PostAsJsonAsync(
            "/api/projects",
            new
            {
                name = "atlas",
                repoUrl = "https://github.com/NaniSoft/atlas",
                workerImage = "ghcr.io/nanisoft/agent-factory-worker:1",
                llmModel = "anthropic/claude-sonnet-4-5",
                githubKeyName = "ATLAS_GITHUB_TOKEN",
                llmKeyName = "ATLAS_ANTHROPIC_API_KEY",
            }))
        {
            response.EnsureSuccessStatusCode();
        }

        // The file exists, in the directory the factory loads from, and says the six
        // values the schema fixes — because the file, not the endpoint, is the source of
        // truth, and the loader's own rules are what make it servable.
        var written = await File.ReadAllTextAsync(Path.Combine(root.FactoriesDirectory, "atlas.yaml"));
        Assert.Contains("name: atlas", written, StringComparison.Ordinal);
        Assert.Contains("model: anthropic/claude-sonnet-4-5", written, StringComparison.Ordinal);
        Assert.Contains("ATLAS_ANTHROPIC_API_KEY", written, StringComparison.Ordinal);

        // And the read is honest about when a project takes effect: the loader reads the
        // directory once at start and does not hot reload, so a project written now is
        // not yet served — the file is the source of truth and a restart is what serves it.
        var listed = await (await host.Board.GetAsync("/api/projects")).Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(listed);
        Assert.DoesNotContain(
            "atlas",
            document.RootElement.GetProperty("projects").EnumerateArray()
                .Select(project => project.GetProperty("name").GetString()),
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_credential_written_through_the_endpoint_is_a_file_and_never_a_rendered_value()
    {
        // The whole of the secrets contract (#32) at the surface: the value goes into the
        // secrets directory as one file named by the credential, and the read — which is
        // the thing that would render it if it ever came back — says only whether a name
        // has one.
        const string secret = "sk-a-value-that-must-never-be-rendered";
        using var root = FactoryRoot.Create()
            .WithProjectFile("nexus.yaml", ProjectFile.Valid);
        await using var host = await FactoryHost.StartAsync(root);

        using (var response = await host.Board.PutAsJsonAsync(
            "/api/credentials/NEXUS_ANTHROPIC_API_KEY",
            new { value = secret }))
        {
            response.EnsureSuccessStatusCode();
        }

        var written = await File.ReadAllTextAsync(
            Path.Combine(Path.GetDirectoryName(root.FactoriesDirectory)!, "secrets", "NEXUS_ANTHROPIC_API_KEY"));
        Assert.Equal(secret, written);

        // Presence is rendered — the served project names the credential and the read says
        // it is set — and the value is not, in the response body, which is everything the
        // app receives.
        var refreshed = await (await host.Board.GetAsync("/api/credentials")).Content.ReadAsStringAsync();
        Assert.Contains("NEXUS_ANTHROPIC_API_KEY", refreshed, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, refreshed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_credential_in_the_secrets_directory_is_read_by_the_factory()
    {
        // The reader is the composite: environment first, directory second. A value the
        // environment does not have but the endpoint wrote is what a round is handed —
        // which is the whole reason the write path exists.
        const string secret = "llm-key-written-by-the-endpoint";
        using var root = FactoryRoot.Create()
            .WithProjectFile("nexus.yaml", ProjectFile.Valid);
        await using var host = await FactoryHost.StartAsync(root);

        using (var response = await host.Board.PutAsJsonAsync(
            "/api/credentials/NEXUS_ANTHROPIC_API_KEY",
            new { value = secret }))
        {
            response.EnsureSuccessStatusCode();
        }

        var throughTheSeam = host.Services.GetRequiredService<ICredentialReader>().Read("NEXUS_ANTHROPIC_API_KEY");
        Assert.Equal(secret, throughTheSeam);
    }
}
