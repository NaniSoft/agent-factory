namespace AgentFactory.Tests.Api;

using System.Net;
using System.Text.Json;
using AgentFactory;
using AgentFactory.Containers;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;

/// <summary>
/// The review workspace's open, driven over the process's real HTTP surface. The
/// factory, the workspace registry, the store and the loop are the real thing; only
/// the Docker CLI is substituted, so what is asserted is what the factory asks the
/// daemon to stand up and what it answers the app with — an active view with a link
/// and a time remaining, or an error, and never silence.
/// </summary>
public class WorkspaceEndpointTests
{
    private const string Nexus = "https://github.com/NaniSoft/nexus";

    [Fact]
    public async Task Opening_a_workspace_in_review_returns_an_active_view_with_a_url()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var docker = new FakeDockerCli();
        await using var host = await FactoryHost.StartWithTheRealRoundAsync(root, docker);
        var workItem = AtReview(host, root);

        using var response = await host.Board.PostAsync($"/api/work-items/{workItem.Id}/workspace", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var view = document.RootElement;

        // The factory's own answer, serialised: open, on the first port of its own
        // loopback range, with the fixed lifetime still whole — and no error.
        Assert.True(view.GetProperty("active").GetBoolean());
        Assert.Equal("http://127.0.0.1:7100/", view.GetProperty("url").GetString());
        Assert.Equal((int)FactoryConstants.WorkspaceLifetime.TotalSeconds, view.GetProperty("remainingSeconds").GetInt32());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("error").ValueKind);

        // And it really stood a container up, at the seam the workspace tests use: a
        // code-server workspace, not a round.
        var create = docker.TheOnly("create");
        Assert.Contains(create, argument => argument.Contains("code-server", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Opening_a_workspace_on_a_non_review_item_returns_an_inactive_view_with_an_error()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var docker = new FakeDockerCli();
        await using var host = await FactoryHost.StartWithTheRealRoundAsync(root, docker);

        // Still in Backlog: there is nothing for a workspace to be a copy of.
        var workItem = host.Store
            .Intake("nexus", Nexus, 42, "A work item, end to end", "What the issue says.", "main")
            .WorkItem;

        using var response = await host.Board.PostAsync($"/api/work-items/{workItem.Id}/workspace", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var view = document.RootElement;

        // Inactive, with the factory's own words for why — and nothing was asked of the
        // daemon, because the work item was refused before any container.
        Assert.False(view.GetProperty("active").GetBoolean());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("url").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("remainingSeconds").ValueKind);
        Assert.Contains(
            "not waiting in Review",
            view.GetProperty("error").GetString(),
            StringComparison.Ordinal);

        // And nothing was asked of the daemon: the refusal happens before a container is
        // ever created. (The sweep at startup is the only thing that has spoken to it.)
        Assert.DoesNotContain(docker.Argvs, argv => argv.FirstOrDefault() == "create");
    }

    [Fact]
    public async Task The_board_card_carries_an_open_workspace_so_a_re_render_shows_it()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var docker = new FakeDockerCli();
        await using var host = await FactoryHost.StartWithTheRealRoundAsync(root, docker);
        var workItem = AtReview(host, root);

        using (await host.Board.PostAsync($"/api/work-items/{workItem.Id}/workspace", content: null))
        {
        }

        // A board read after the open carries the workspace on the card, so a renderer
        // that refetches shows the link and the time left rather than an "Open workspace"
        // button whose click was forgotten.
        using var response = await host.Board.GetAsync("/api/board");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var review = document.RootElement
            .GetProperty("lanes")
            .EnumerateArray()
            .Single(lane => lane.GetProperty("lane").GetString() == "Review");
        var card = Assert.Single(review.GetProperty("cards").EnumerateArray());

        var workspace = card.GetProperty("workspace");
        Assert.True(workspace.GetProperty("active").GetBoolean());
        Assert.Equal("http://127.0.0.1:7100/", workspace.GetProperty("url").GetString());
        Assert.Equal((int)FactoryConstants.WorkspaceLifetime.TotalSeconds, workspace.GetProperty("remainingSeconds").GetInt32());
        Assert.Equal(JsonValueKind.Null, workspace.GetProperty("error").ValueKind);
    }

    /// <summary>
    /// A work item waiting in Review with the round tree the runtime would have lifted
    /// for it, laid out where the workspace reads it. It is moved rather than built,
    /// because this ticket is the workspace and not the round — the tree it copies is
    /// the only thing the workspace needs from a round.
    /// </summary>
    private static WorkItem AtReview(FactoryHost host, FactoryRoot root)
    {
        var workItem = host.Store
            .Intake("nexus", Nexus, 42, "A work item, end to end", "What the issue says.", "main")
            .WorkItem;
        host.Store.Move(workItem.Id, Swimlane.Review);

        var tree = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(root.DatabasePath))!,
            "rounds",
            workItem.Id.ToString("N"),
            $"round-{workItem.RoundCount}",
            "attempt-1",
            ContainerRuntime.RoundTreeFolder);
        Directory.CreateDirectory(Path.Combine(tree, ".git"));

        return workItem;
    }
}
