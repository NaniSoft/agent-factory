namespace AgentFactory.Tests.Api;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentFactory.Tests.Boundary;

/// <summary>
/// The credentials surface, driven over the process's real HTTP surface. The factory,
/// the secrets directory and the composite reader are the real thing; what is asserted
/// is exactly what a renderer reads and the one thing it never does: the value. Names
/// and presence travel; a value goes in and is never rendered back (#32, #49).
/// </summary>
public class CredentialsEndpointTests
{
    private const string Project = "probe";
    private const string RepoUrl = "https://github.com/NaniSoft/probe";
    private const string Declared = "PROBE_GITHUB_TOKEN";
    private const string DeclaredLlm = "PROBE_ANTHROPIC_API_KEY";

    [Fact]
    public async Task The_credentials_endpoint_lists_declared_names_with_set_or_unset()
    {
        using var root = FactoryRoot.Create()
            .WithProjectFile("probe.yaml", ProjectFile.For(Project, RepoUrl));
        await using var host = await FactoryHost.StartAsync(root);

        using var response = await host.Board.GetAsync("/api/credentials");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var entries = document.RootElement.GetProperty("credentials").EnumerateArray().ToList();

        // Both keys the served project declares, and neither has a value yet.
        Assert.Contains(Declared, entries.Select(entry => entry.GetProperty("name").GetString()));
        Assert.Contains(DeclaredLlm, entries.Select(entry => entry.GetProperty("name").GetString()));
        var declared = entries.Single(entry => entry.GetProperty("name").GetString() == Declared);
        Assert.False(declared.GetProperty("set").GetBoolean());

        // Names and presence only: the response carries no value property at all.
        Assert.All(entries, entry => Assert.Equal(2, entry.EnumerateObject().Count()));
    }

    [Fact]
    public async Task A_credential_written_through_the_endpoint_is_a_trimmed_file_and_never_a_rendered_value()
    {
        const string secret = "sk-a-value-that-must-never-be-rendered";
        using var root = FactoryRoot.Create()
            .WithProjectFile("probe.yaml", ProjectFile.For(Project, RepoUrl));
        await using var host = await FactoryHost.StartAsync(root);

        using var response = await host.Board.PutAsJsonAsync(
            $"/api/credentials/{DeclaredLlm}",
            new { value = $"  {secret}  \n" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The value went into the secrets directory, trimmed, and came back nowhere.
        var answer = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, answer, StringComparison.Ordinal);

        var file = Path.Combine(Path.GetDirectoryName(root.FactoriesDirectory)!, "secrets", DeclaredLlm);
        Assert.True(File.Exists(file), "the write did not land in the secrets directory");
        Assert.Equal(secret, await File.ReadAllTextAsync(file));

        // A subsequent read says it is set, and still never says what it is.
        var listed = await (await host.Board.GetAsync("/api/credentials")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, listed, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(listed);
        var entry = document.RootElement.GetProperty("credentials")
            .EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == DeclaredLlm);
        Assert.True(entry.GetProperty("set").GetBoolean());
    }

    [Fact]
    public async Task A_write_to_a_name_the_factory_would_not_serve_is_refused_and_writes_nothing()
    {
        // The name arrives from a route segment, so a write is guarded exactly as a delete
        // is: a name that would not be a file the factory serves is refused rather than
        // handed to a write that could create a path outside the secrets directory.
        using var root = FactoryRoot.Create()
            .WithProjectFile("probe.yaml", ProjectFile.For(Project, RepoUrl));
        await using var host = await FactoryHost.StartAsync(root);

        using var response = await host.Board.PutAsJsonAsync(
            "/api/credentials/not%20servable",
            new { value = "a-value-that-must-not-land" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var secrets = Path.Combine(Path.GetDirectoryName(root.FactoriesDirectory)!, "secrets");
        Assert.False(
            Directory.Exists(secrets) && Directory.EnumerateFiles(secrets).Any(),
            "an unservable name must not create a file in the secrets directory");
    }

    [Fact]
    public async Task A_credential_file_present_but_not_declared_is_listed_and_removed()
    {
        using var root = FactoryRoot.Create()
            .WithProjectFile("probe.yaml", ProjectFile.For(Project, RepoUrl));
        var secrets = Path.Combine(Path.GetDirectoryName(root.FactoriesDirectory)!, "secrets");
        Directory.CreateDirectory(secrets);
        await File.WriteAllTextAsync(Path.Combine(secrets, "AN_ORPHANED_TOKEN"), "a-value-nobody-routed");

        await using var host = await FactoryHost.StartAsync(root);

        // A file present in the directory is a credential the factory can use even though
        // no served project names it, so it is on the list.
        var listed = await (await host.Board.GetAsync("/api/credentials")).Content.ReadAsStringAsync();
        Assert.Contains("AN_ORPHANED_TOKEN", listed, StringComparison.Ordinal);

        using var response = await host.Board.DeleteAsync("/api/credentials/AN_ORPHANED_TOKEN");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(secrets, "AN_ORPHANED_TOKEN")));

        var after = await (await host.Board.GetAsync("/api/credentials")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("AN_ORPHANED_TOKEN", after, StringComparison.Ordinal);
    }
}
