namespace AgentFactory.Tests.Review;

using AgentFactory;
using AgentFactory.Containers;
using AgentFactory.Projects;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;
using AgentFactory.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The review workspace, at the seam the suite trusts: the Docker CLI is faked, the
/// registry and the clock are real, and what is under test is what the factory asks the
/// daemon to stand up and when it takes it away — the contract the exposure decision
/// (#30) and the workspace contract (#31) fixed, and the slice (#35) implements.
/// </summary>
public class ReviewWorkspaceTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";

    [Fact]
    public async Task Opening_a_workspace_stands_a_container_up_that_is_not_a_round()
    {
        using var harness = Harness.AtReview();

        await harness.Workspaces.OpenAsync(harness.WorkItem.Id, CancellationToken.None);

        var create = harness.Docker.TheOnly("create");

        // From the project's image — the toolchain the reviewer is verifying with — and
        // named after the work item, so two workspaces cannot be confused and a leftover
        // one can always be found by name.
        Assert.Equal($"agent-factory-workspace-{harness.WorkItem.Id:N}", create[2]);
        Assert.Contains("ghcr.io/nanisoft/agent-factory-worker:1", create);

        // Not a round. The round entrypoint is the image's default; a workspace
        // overrides it, writes no result file and runs no recording wrapper — it is a
        // second kind of container sharing the image, and the command it runs is
        // code-server and nothing else.
        Assert.DoesNotContain(create, argument => argument.Contains("worker-round", StringComparison.Ordinal));
        Assert.Contains(create, argument => argument.Contains("code-server", StringComparison.Ordinal));
        Assert.Contains(create, argument => argument.Contains("--auth none", StringComparison.Ordinal));

        // And it holds no credential: nothing in the create passes an environment value,
        // because there is no push path from a workspace and nothing to authenticate with.
        Assert.DoesNotContain(create, argument => argument.StartsWith("-e", StringComparison.Ordinal));
        Assert.DoesNotContain(create, argument => argument.Contains("API_KEY", StringComparison.Ordinal));
        Assert.DoesNotContain(create, argument => argument.Contains("TOKEN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_workspace_is_published_on_loopback_from_the_factorys_own_range()
    {
        // The exposure decision (#30): the loopback is the gate, the range is code, and
        // the host binding never leaves the machine. The first workspace takes the first
        // port; a second work item's workspace takes the next.
        using var harness = Harness.AtReview();
        var secondItem = harness.AddWorkItemAtReview(43);

        await harness.Workspaces.OpenAsync(harness.WorkItem.Id, CancellationToken.None);
        Assert.Contains(
            harness.Docker.TheOnly("create"),
            argument => argument == "127.0.0.1:7100:6800");

        await harness.Workspaces.OpenAsync(secondItem.Id, CancellationToken.None);
        var creates = harness.Docker.Argvs.Where(argv => argv.FirstOrDefault() == "create").ToList();
        Assert.Equal(2, creates.Count);
        Assert.Contains(creates[1], argument => argument == "127.0.0.1:7101:6800");
    }

    [Fact]
    public async Task The_workspaces_tree_is_a_copy_of_the_rounds_and_the_push_source_is_untouched()
    {
        // The contract's sharpest clause: the workspace gets a copy, because the host's
        // tree is what the merger pushes, and a reviewer editing inside a workspace must
        // never be able to change what an approve ships.
        using var harness = Harness.AtReview();
        var treeFile = Path.Combine(harness.TreePath, "src", "Index.cs");
        var before = await File.ReadAllTextAsync(treeFile);

        await harness.Workspaces.OpenAsync(harness.WorkItem.Id, CancellationToken.None);

        var cp = harness.Docker.TheOnly("cp");
        Assert.Equal("cp", cp[0]);
        Assert.EndsWith("/.", cp[1], StringComparison.Ordinal);
        Assert.StartsWith(Path.GetFullPath(harness.TreePath), cp[1], StringComparison.Ordinal);
        Assert.EndsWith(":/work", cp[2], StringComparison.Ordinal);

        // The host's own tree is exactly what it was.
        Assert.Equal(before, await File.ReadAllTextAsync(treeFile));
    }

    [Fact]
    public async Task Opening_twice_answers_with_the_workspace_that_exists_rather_than_a_second_one()
    {
        using var harness = Harness.AtReview();
        await harness.Workspaces.OpenAsync(harness.WorkItem.Id, CancellationToken.None);
        await harness.Workspaces.OpenAsync(harness.WorkItem.Id, CancellationToken.None);

        harness.Docker.TheOnly("create");

        var view = harness.Workspaces.ViewFor(harness.WorkItem.Id);
        Assert.True(view?.Active);
        Assert.Equal("http://127.0.0.1:7100/", view?.Url);
    }

    [Fact]
    public async Task A_workspace_past_its_lifetime_is_taken_away_and_its_port_returned()
    {
        // A fixed lifetime from spawn, rendered while it lasts (#31). Nothing inside a
        // workspace can report idleness, so the clock is the only witness, and the sweep
        // is what acts on it.
        using var harness = Harness.AtReview();
        await harness.Workspaces.OpenAsync(harness.WorkItem.Id, CancellationToken.None);

        harness.Clock.Advance(FactoryConstants.WorkspaceLifetime);
        await harness.Workspaces.SweepAsync(CancellationToken.None);

        Assert.Equal(["rm", "-f", $"agent-factory-workspace-{harness.WorkItem.Id:N}"], harness.Docker.TheOnly("rm"));
        Assert.True(harness.Workspaces.ViewFor(harness.WorkItem.Id) is not { Active: true });

        // The port went back: the next workspace a reviewer opens gets the first port of
        // the range again — the last create, not the first, is the re-open's.
        await harness.Workspaces.OpenAsync(harness.WorkItem.Id, CancellationToken.None);
        var lastCreate = harness.Docker.Argvs.Last(argv => argv.FirstOrDefault() == "create");
        Assert.Contains(lastCreate, argument => argument == "127.0.0.1:7100:6800");
    }

    [Fact]
    public async Task A_workspace_whose_work_item_is_no_longer_in_review_is_taken_away_on_the_next_sweep()
    {
        // The decision is the workspace's reason to exist. Approve, reject, or a round
        // starting — the item left Review, and the tree it copied went stale the instant
        // the next round began.
        using var harness = Harness.AtReview();
        await harness.Workspaces.OpenAsync(harness.WorkItem.Id, CancellationToken.None);

        harness.Store.Move(harness.WorkItem.Id, Swimlane.Done);
        await harness.Workspaces.SweepAsync(CancellationToken.None);

        Assert.Equal(["rm", "-f", $"agent-factory-workspace-{harness.WorkItem.Id:N}"], harness.Docker.TheOnly("rm"));
    }

    [Fact]
    public async Task A_workspace_that_could_not_be_created_is_a_rendered_state_and_releases_its_port()
    {
        // The failure path is a rendered state, never silence (#31) — and the port a
        // failed spawn took is not leaked out of the range.
        using var harness = Harness.AtReview();
        harness.Docker.Handler = call => call.Arguments.FirstOrDefault() == "create"
            ? Task.FromResult(new DockerInvocation(125, "Error response from daemon: no such image"))
            : Task.FromResult(new DockerInvocation(0, string.Empty));

        await harness.Workspaces.OpenAsync(harness.WorkItem.Id, CancellationToken.None);

        var view = harness.Workspaces.ViewFor(harness.WorkItem.Id);
        Assert.False(view?.Active ?? true);
        Assert.Contains("no such image", view?.Error, StringComparison.OrdinalIgnoreCase);

        harness.Docker.Reset();
        await harness.Workspaces.OpenAsync(harness.WorkItem.Id, CancellationToken.None);
        Assert.Contains(
            harness.Docker.TheOnly("create"),
            argument => argument == "127.0.0.1:7100:6800");
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _root;

        private Harness(
            string root,
            FakeDockerCli docker,
            ReviewWorkspaces workspaces,
            WorkItem workItem,
            string treePath,
            TestClock clock,
            SqliteWorkItemStore store,
            string storePath)
        {
            _root = root;
            Docker = docker;
            Workspaces = workspaces;
            WorkItem = workItem;
            TreePath = treePath;
            Clock = clock;
            Store = store;
            StorePath = storePath;
        }

        public FakeDockerCli Docker { get; }

        public ReviewWorkspaces Workspaces { get; }

        public WorkItem WorkItem { get; }

        public string TreePath { get; }

        public TestClock Clock { get; }

        public SqliteWorkItemStore Store { get; }

        public string StorePath { get; }

        /// <summary>One more work item sitting in Review, for the concurrency cases. Its
        /// round tree is laid out where the runtime would have lifted one to.</summary>
        public WorkItem AddWorkItemAtReview(int issueNumber)
        {
            var workItem = Store
                .Intake("nexus", RepoUrl, issueNumber, "Another work item", "What the issue says.", "main")
                .WorkItem;
            Store.Move(workItem.Id, Swimlane.Review);

            var tree = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(StorePath))!,
                "rounds",
                workItem.Id.ToString("N"),
                "round-0",
                "attempt-1",
                ContainerRuntime.RoundTreeFolder);
            Directory.CreateDirectory(Path.Combine(tree, ".git"));
            return workItem;
        }

        public static Harness AtReview()
        {
            var root = Path.Combine(
                Path.GetTempPath(), "agent-factory-workspace-tests", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(root);

            var docker = new FakeDockerCli();
            var clock = new TestClock();

            var databasePath = Path.Combine(root, "agent-factory.db");
            var store = new SqliteWorkItemStore(databasePath, clock);
            var workItem = store
                .Intake("nexus", RepoUrl, 42, "A work item, end to end", "What the issue says.", "main")
                .WorkItem;
            store.Move(workItem.Id, Swimlane.Review);

            // A round's lifted tree, exactly where the runtime leaves one for this work
            // item — the workspace reads the same layout the merger does.
            var treePath = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(databasePath))!,
                "rounds",
                workItem.Id.ToString("N"),
                "round-0",
                "attempt-1",
                ContainerRuntime.RoundTreeFolder);
            Directory.CreateDirectory(Path.Combine(treePath, ".git"));
            Directory.CreateDirectory(Path.Combine(treePath, "src"));
            File.WriteAllText(Path.Combine(treePath, "src", "Index.cs"), "public class Index {}\n");

            var projects = new ProjectLoadReport(
                [
                    new Project(
                        "nexus",
                        RepoUrl,
                        "ghcr.io/nanisoft/agent-factory-worker:1",
                        ProjectFile.Model,
                        "NEXUS_GITHUB_TOKEN",
                        "NEXUS_ANTHROPIC_API_KEY",
                        "nexus.yaml"),
                ],
                []);

            var options = new FactoryOptions(
                Path.Combine(root, "factories"),
                databasePath,
                new Uri("http://127.0.0.1:0"));

            var workspaces = new ReviewWorkspaces(
                docker,
                store,
                clock,
                projects,
                options,
                NullLogger<ReviewWorkspaces>.Instance);

            return new Harness(root, docker, workspaces, workItem, treePath, clock, store, databasePath);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
