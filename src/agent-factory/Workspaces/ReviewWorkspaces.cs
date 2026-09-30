namespace AgentFactory.Workspaces;

using AgentFactory.Clock;
using AgentFactory.Containers;
using AgentFactory.Projects;
using AgentFactory.WorkItems;
using Microsoft.Extensions.Logging;

/// <summary>
/// The review workspaces a reviewer has asked for: at most one container per work item
/// in Review, running code-server and the project's toolchain on a copy of the round's
/// tree, published on loopback from the factory's own port range, holding no credential,
/// and taken away on the decision, on the next round, or at a fixed lifetime.
/// </summary>
/// <remarks>
/// <para>
/// A review workspace is <strong>not a round</strong>. It is a second kind of container
/// that shares the project's image and nothing else: the round entrypoint is overridden,
/// no result file is written, the recording wrapper does not run, and nothing inside it
/// can reach a credential — which is the same sentence as "there is no push path", because
/// the host pushes (ADR-0006) and the workspace is not the host.
/// </para>
/// <para>
/// The tree it holds is a <em>copy</em>, taken with <c>docker cp</c> from the landing
/// directory the round's tree was lifted into. The host's own tree is the merger's push
/// source; sharing it with a container a reviewer is free to edit in would put a
/// workspace's changes on the next approve. The copy direction also means a reviewer's
/// session is disposable by construction — opening another is a click, and it reflects
/// whatever the latest round left.
/// </para>
/// <para>
/// The registry is in memory and the sweep is the clock's, not a timer's: the driver's
/// tick asks for a sweep, entries past their lifetime or whose work item has left Review
/// are removed by name, and their ports return to the range. A container from a process
/// that died is swept at startup by name — a workspace's name is the work item's, so a
/// leftover is always recognisable as this factory's own.
/// </para>
/// </remarks>
public sealed class ReviewWorkspaces
{
    /// <summary>The prefix every workspace container is named under.</summary>
    public const string NamePrefix = "agent-factory-workspace-";

    private readonly IDockerCli _docker;
    private readonly IWorkItemStore _store;
    private readonly IClock _clock;
    private readonly ProjectLoadReport _projects;
    private readonly FactoryOptions _options;
    private readonly ILogger<ReviewWorkspaces> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _active = [];

    public ReviewWorkspaces(
        IDockerCli docker,
        IWorkItemStore store,
        IClock clock,
        ProjectLoadReport projects,
        FactoryOptions options,
        ILogger<ReviewWorkspaces> logger)
    {
        _docker = docker ?? throw new ArgumentNullException(nameof(docker));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>What the board renders about one work item's workspace, if anything.</summary>
    public sealed record View(bool Active, string? Url, TimeSpan? Remaining, string? Error);

    /// <summary>Where the latest round's tree lands, derived the way the runtime lays it out.</summary>
    private string TreeFor(WorkItem workItem)
    {
        var landing = Path.Combine(
            _options.RoundsDirectory,
            workItem.Id.ToString("N"),
            $"round-{workItem.RoundCount}",
            "attempt-1");
        return Path.Combine(landing, ContainerRuntime.RoundTreeFolder);
    }

    /// <summary>
    /// Opens (or answers with) the workspace for a work item in Review. A spawn that
    /// cannot happen is a <see cref="View"/> with an error on it and nothing else changed.
    /// </summary>
    public async Task<View> OpenAsync(Guid workItemId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(workItemId, out var existing) && existing.Error is null)
            {
                return ViewOf(existing);
            }
        }

        var workItem = _store.List().FirstOrDefault(item => item.Id == workItemId);
        if (workItem is null || workItem.Swimlane != Swimlane.Review)
        {
            return new View(false, null, null, "the work item is not waiting in Review, so it has no workspace to open");
        }

        var project = _projects.Projects.FirstOrDefault(candidate => candidate.Name == workItem.Project);
        if (project is null)
        {
            return new View(false, null, null, $"no project file is being served for {workItem.Project}");
        }

        var tree = TreeFor(workItem);
        if (!Directory.Exists(Path.Combine(tree, ".git")))
        {
            return new View(false, null, null, "the latest round left no tree to open");
        }

        var port = TakePort();
        if (port is null)
        {
            return new View(false, null, null,
                $"every port from {FactoryConstants.WorkspacePortFloor} to {FactoryConstants.WorkspacePortCeiling} is taken; "
                + "close a workspace and open this one again");
        }

        var name = $"{NamePrefix}{workItemId:N}";
        try
        {
            await CreateAsync(name, project, port.Value, cancellationToken);
            await CopyTreeAsync(name, tree, cancellationToken);
            await StartAsync(name, cancellationToken);
        }
        catch (Exception refused)
        {
            // The failure is remembered where the board reads it — a workspace that could
            // not be created is a rendered state, never silence, and the diff view
            // remains the reviewer's surface either way (#31). The port goes back: an
            // entry that carries no workspace holds no port either.
            _logger.LogWarning(
                "A review workspace for {Project}#{Issue} could not be created: {Reason}",
                workItem.Project,
                workItem.IssueNumber,
                refused.Message);

            await RemoveQuietly(name);
            lock (_gate)
            {
                _active[workItemId] = new Entry(workItemId, name, 0, _clock.UtcNow, refused.Message);
            }

            return new View(false, null, null, refused.Message);
        }

        var entry = new Entry(workItemId, name, port.Value, _clock.UtcNow);
        lock (_gate)
        {
            _active[workItemId] = entry;
        }

        _logger.LogInformation(
            "A review workspace for {Project}#{Issue} is open at {Url} — code-server on the round's tree, "
                + "no credential inside, and it ends on the decision or in {Lifetime}.",
            workItem.Project,
            workItem.IssueNumber,
            $"http://127.0.0.1:{port.Value}/",
            FactoryConstants.WorkspaceLifetime);

        return ViewOf(entry);
    }

    /// <summary>What the board shows for a work item's workspace, or null for nothing.</summary>
    public View? ViewFor(Guid workItemId)
    {
        lock (_gate)
        {
            return _active.TryGetValue(workItemId, out var entry) ? ViewOf(entry) : null;
        }
    }

    /// <summary>
    /// Takes away what the clock or the board says is finished: workspaces past their
    /// lifetime, and workspaces whose work item has left Review.
    /// </summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        List<Entry> finished;
        lock (_gate)
        {
            finished = _active.Values
                .Where(entry => _clock.UtcNow - entry.SpawnedAtUtc >= FactoryConstants.WorkspaceLifetime
                    || _store.List().Any(item => item.Id == entry.WorkItemId && item.Swimlane != Swimlane.Review))
                .ToList();
        }

        foreach (var entry in finished)
        {
            await RemoveAsync(entry, cancellationToken);
        }
    }

    /// <summary>
    /// Removes any workspace container a previous process left behind. A workspace is not
    /// in the store of record and an entry in a dead process's memory is gone, but the
    /// container it started is still running on the daemon — its name is how it owns up to
    /// being this factory's own.
    /// </summary>
    public async Task SweepOrphansAsync(CancellationToken cancellationToken)
    {
        var listed = await _docker.InvokeAsync(
            ["ps", "-a", "--format", "{{.Names}}"],
            output: null,
            cancellationToken);
        if (listed.ExitCode != 0)
        {
            return;
        }

        foreach (var name in listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => name.StartsWith(NamePrefix, StringComparison.Ordinal)))
        {
            _logger.LogWarning(
                "A review workspace from a previous run of the factory is still on the daemon; removing {Name}.",
                name);
            await _docker.InvokeAsync(["rm", "-f", name], output: null, cancellationToken);
        }
    }

    private async Task CreateAsync(string name, Project project, int port, CancellationToken cancellationToken)
    {
        // Root is the user that can hand the copied tree to the reviewer's user; the
        // command drops back to the image's own unprivileged user before code-server
        // starts, so the editor runs as uid 1000 the way every round does (ADR-0012's
        // compensating controls). There is nothing to authenticate with and nothing to
        // push with: the container holds no credential and the loopback is the gate (#30).
        var command = "chown -R agent:agent /work 2>/dev/null; "
            + $"exec setpriv --reuid=agent --regid=agent --init-groups "
            + $"code-server --auth none --bind-addr 0.0.0.0:{FactoryConstants.CodeServerPort} /work";

        var created = await _docker.InvokeAsync(
            [
                "create",
                "--name",
                name,
                "--user",
                "root",
                "--publish",
                $"127.0.0.1:{port}:{FactoryConstants.CodeServerPort}",
                project.WorkerImage,
                "bash",
                "-c",
                command,
            ],
            output: null,
            cancellationToken);
        if (created.ExitCode != 0)
        {
            throw new InvalidOperationException($"the workspace's container could not be created ({created.Output})");
        }
    }

    private async Task CopyTreeAsync(string name, string tree, CancellationToken cancellationToken)
    {
        // Into the container this time: the round's tree, whole, at /work. The trailing
        // `/.` is what copies the contents rather than nesting a directory — the same
        // rule #22 recorded about `docker cp`, read in the other direction.
        var lifted = await _docker.InvokeAsync(
            ["cp", $"{tree}/.", $"{name}:{ContainerRuntime.WorkPathInContainer}"],
            output: null,
            cancellationToken);
        if (lifted.ExitCode != 0)
        {
            throw new InvalidOperationException($"the workspace's tree could not be copied ({lifted.Output})");
        }
    }

    private async Task StartAsync(string name, CancellationToken cancellationToken)
    {
        var started = await _docker.InvokeAsync(["start", name], output: null, cancellationToken);
        if (started.ExitCode != 0)
        {
            throw new InvalidOperationException($"the workspace's container could not be started ({started.Output})");
        }
    }

    private async Task RemoveAsync(Entry entry, CancellationToken cancellationToken)
    {
        await _docker.InvokeAsync(["rm", "-f", entry.Name], output: null, cancellationToken);
        lock (_gate)
        {
            _active.Remove(entry.WorkItemId);
        }
    }

    private async Task RemoveQuietly(string name)
    {
        try
        {
            await _docker.InvokeAsync(["rm", "-f", name], output: null, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A container that is already gone is the outcome this is hoping for.
        }
    }

    private int? TakePort()
    {
        lock (_gate)
        {
            var taken = _active.Values
                .Where(entry => entry.Error is null)
                .Select(entry => entry.Port)
                .ToHashSet();
            for (var port = FactoryConstants.WorkspacePortFloor; port <= FactoryConstants.WorkspacePortCeiling; port++)
            {
                if (!taken.Contains(port))
                {
                    return port;
                }
            }

            return null;
        }
    }

    private View ViewOf(Entry entry) => entry.Error is { } error
        ? new View(false, null, null, error)
        : new View(
            true,
            $"http://127.0.0.1:{entry.Port}/",
            FactoryConstants.WorkspaceLifetime - (_clock.UtcNow - entry.SpawnedAtUtc),
            null);

    private sealed record Entry(Guid WorkItemId, string Name, int Port, DateTimeOffset SpawnedAtUtc, string? Error = null);
}
