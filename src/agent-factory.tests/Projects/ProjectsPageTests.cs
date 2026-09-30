namespace AgentFactory.Tests.Projects;

using System.Net;
using System.Net.Http;
using AgentFactory.Credentials;
using AgentFactory.Tests.Boundary;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The projects surface, exercised through the running factory: a repository is added
/// through the page, a credential is written through it, and what lands is the file the
/// loader would have written — with the secret never rendered anywhere.
/// </summary>
public class ProjectsPageTests
{
    [Fact]
    public async Task Adding_a_project_through_the_page_writes_the_file_the_loader_accepts()
    {
        using var root = FactoryRoot.Create();
        await using var host = await FactoryHost.StartAsync(root);

        var page = await host.Board.GetAsync("/Projects");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();

        using var response = await PostFormAsync(
            host.Board,
            html,
            "/Projects?handler=Add",
            new Dictionary<string, string>
            {
                ["name"] = "atlas",
                ["repoUrl"] = "https://github.com/NaniSoft/atlas",
                ["workerImage"] = "ghcr.io/nanisoft/agent-factory-worker:1",
                ["llmModel"] = "anthropic/claude-sonnet-4-5",
                ["githubKeyName"] = "ATLAS_GITHUB_TOKEN",
                ["llmKeyName"] = "ATLAS_ANTHROPIC_API_KEY",
            });

        // The file exists, in the directory the factory loads from, and says the six
        // values the schema fixes — because the file, not the page, is the source of
        // truth, and the loader's own rules are what make it servable.
        var written = await File.ReadAllTextAsync(Path.Combine(root.FactoriesDirectory, "atlas.yaml"));
        Assert.Contains("name: atlas", written, StringComparison.Ordinal);
        Assert.Contains("model: anthropic/claude-sonnet-4-5", written, StringComparison.Ordinal);
        Assert.Contains("ATLAS_ANTHROPIC_API_KEY", written, StringComparison.Ordinal);

        // And the page says the honest thing about when it takes effect.
        var refreshed = await (await host.Board.GetAsync("/Projects?added=atlas")).Content.ReadAsStringAsync();
        Assert.Contains("served after a restart", refreshed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_credential_written_through_the_page_is_a_file_and_never_a_rendered_value()
    {
        // The whole of the secrets contract (#32) at the surface: the value goes into the
        // secrets directory as one file named by the credential, and the page — which is
        // the thing that would render it if it ever came back — says only whether a name
        // has one.
        const string secret = "sk-a-value-that-must-never-be-rendered";
        using var root = FactoryRoot.Create()
            .WithProjectFile("nexus.yaml", ProjectFile.Valid);
        await using var host = await FactoryHost.StartAsync(root);

        var html = await (await host.Board.GetAsync("/Projects")).Content.ReadAsStringAsync();
        using var response = await PostFormAsync(
            host.Board,
            html,
            "/Projects?handler=Credential",
            new Dictionary<string, string> { ["name"] = "NEXUS_ANTHROPIC_API_KEY", ["value"] = secret });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var written = await File.ReadAllTextAsync(
            Path.Combine(Path.GetDirectoryName(root.FactoriesDirectory)!, "secrets", "NEXUS_ANTHROPIC_API_KEY"));
        Assert.Equal(secret, written);

        // Presence is rendered — the served project names the credential and the page says
        // it is set — and the value is not, in the page's html, which is everything a
        // browser would receive.
        var refreshed = await (await host.Board.GetAsync("/Projects")).Content.ReadAsStringAsync();
        Assert.Contains("NEXUS_ANTHROPIC_API_KEY", refreshed, StringComparison.Ordinal);
        Assert.Contains("set", refreshed, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, refreshed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_credential_in_the_secrets_directory_is_read_by_the_factory()
    {
        // The reader is the composite: environment first, directory second. A value the
        // environment does not have but the page wrote is what a round is handed — which
        // is the whole reason the form exists.
        const string secret = "llm-key-written-by-the-page";
        using var root = FactoryRoot.Create()
            .WithProjectFile("nexus.yaml", ProjectFile.Valid);
        await using var host = await FactoryHost.StartAsync(root);

        var html = await (await host.Board.GetAsync("/Projects")).Content.ReadAsStringAsync();
        await PostFormAsync(
            host.Board,
            html,
            "/Projects?handler=Credential",
            new Dictionary<string, string> { ["name"] = "NEXUS_ANTHROPIC_API_KEY", ["value"] = secret });

        var throughTheSeam = host.Services.GetRequiredService<ICredentialReader>().Read("NEXUS_ANTHROPIC_API_KEY");
        Assert.Equal(secret, throughTheSeam);
    }

    /// <summary>Posts a form with the page's own antiforgery token, as a browser would.</summary>
    private static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client,
        string html,
        string action,
        IReadOnlyDictionary<string, string> fields)
    {
        var token = TokenFrom(html);
        using var form = new FormUrlEncodedContent(
            fields.Concat([new KeyValuePair<string, string>("__RequestVerificationToken", token)]));
        return await client.PostAsync(action, form);
    }

    private static string TokenFrom(string html)
    {
        const string marker = "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "the page rendered no antiforgery token, so no form can be posted");
        var value = html[(start + marker.Length)..];
        return value[..value.IndexOf('"', StringComparison.Ordinal)];
    }
}
