namespace AgentFactory.App.Fleet;

/// <summary>
/// Host-owned git flow (ADR 0001): per-repo clones under
/// <c>&lt;CloneRoot&gt;/&lt;owner&gt;__&lt;name&gt;</c>, worktrees cut from them
/// keyed the same way (ticket 017), branch inspection, cleanup of fleet-local
/// config, and the push that precedes a PR. Agents commit inside the worktree;
/// only the host pushes.
/// </summary>
public sealed class GitService(FleetOptions options)
{
    public const string FleetConfigFileName = "opencode.json";

    /// <summary>Directory-safe key for a repo identity: owner/name →
    /// owner__name (invalid path chars defensively scrubbed).</summary>
    public static string RepoKey(string repo)
    {
        var key = repo.Replace('/', '_');
        return new string(key.Select(c => Path.GetInvalidPathChars().Contains(c) || c is '\\' or ':' ? '-' : c).ToArray());
    }

    /// <summary>Local clone for a repo, under the clone root.</summary>
    public string ClonePathFor(string repo) =>
        Path.Combine(options.CloneRoot, RepoKey(repo));

    private static string RepoUrl(string repo) => $"https://github.com/{repo}.git";

    /// <summary>
    /// Branch for one attempt, unique per run (a re-trigger re-runs an issue and
    /// must not collide with the first run's local/remote branch):
    /// agent/issue-&lt;n&gt;-&lt;runSeq&gt;[-retry].
    /// </summary>
    public string BranchFor(int issueNumber, int attempt, string runId) =>
        $"agent/issue-{issueNumber}-{RunSeq(runId)}" + (attempt <= 1 ? "" : "-retry");

    /// <summary>Worktree path for an attempt, unique per run, under the
    /// repo-keyed subtree of the worktree root.</summary>
    public string WorktreeFor(string repo, int issueNumber, int attempt, string runId) =>
        Path.Combine(options.WorktreeRoot, RepoKey(repo), $"issue-{issueNumber}", $"{RunSeq(runId)}-attempt-{attempt}");

    private static string RunSeq(string runId) => runId.StartsWith("run-") ? runId["run-".Length..] : runId;

    public async Task EnsureCloneAsync(string repo, CancellationToken ct = default)
    {
        var clonePath = ClonePathFor(repo);
        if (Directory.Exists(clonePath) && Directory.Exists(Path.Combine(clonePath, ".git")))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(clonePath)!);
        var result = await Proc.RunAsync("git", $"clone {RepoUrl(repo)} \"{clonePath}\"", ct: ct);
        if (!result.Ok) throw new InvalidOperationException($"clone of {repo} failed: {result.Output}");
    }

    /// <summary>Cut a fresh worktree on a new branch off <c>origin/baseRef</c>
    /// (the fleet base branch by default; a chained child cuts from the
    /// previous child's pushed branch, ticket 020). Self-healing: a stale
    /// worktree or branch at the same names (e.g. from a crashed earlier
    /// attempt) is removed first — the host owns the git flow.</summary>
    public async Task<ProcResult> CreateWorktreeAsync(
        string repo, int issueNumber, int attempt, string runId,
        string? baseRef = null, CancellationToken ct = default)
    {
        baseRef ??= options.BaseBranch;
        var clonePath = ClonePathFor(repo);
        var fetch = await Proc.RunAsync("git", $"fetch origin {baseRef}", clonePath, ct);
        if (!fetch.Ok) return fetch;

        var worktree = WorktreeFor(repo, issueNumber, attempt, runId);
        var branch = BranchFor(issueNumber, attempt, runId);
        if (Directory.Exists(worktree))
        {
            await Proc.RunAsync("git", $"worktree remove --force \"{worktree}\"", clonePath, ct);
        }
        await Proc.RunAsync("git", $"branch -D {branch}", clonePath, ct); // may not exist: fine
        await Proc.RunAsync("git", "worktree prune", clonePath, ct);

        Directory.CreateDirectory(Path.GetDirectoryName(worktree)!);
        return await Proc.RunAsync("git",
            $"worktree add \"{worktree}\" -b {branch} origin/{baseRef}",
            clonePath, ct);
    }

    /// <summary>Commits the branch carries on top of its base ref.</summary>
    public async Task<int> CountCommitsAsync(
        string worktree, string branch, string? baseRef = null, CancellationToken ct = default)
    {
        var result = await Proc.RunAsync("git",
            $"rev-list --count origin/{baseRef ?? options.BaseBranch}..{branch}", worktree, ct);
        return result.Ok && int.TryParse(result.Output.Split('\n')[0].Trim(), out var n) ? n : 0;
    }

    /// <summary>Files changed vs base (one per line, empty if none).</summary>
    public async Task<List<string>> ChangedFilesAsync(
        string worktree, string? baseRef = null, CancellationToken ct = default)
    {
        var result = await Proc.RunAsync("git",
            $"diff --name-only origin/{baseRef ?? options.BaseBranch}...HEAD", worktree, ct);
        return result.Ok
            ? result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : [];
    }

    /// <summary>
    /// If the agent committed the fleet-local permission config, strip it with a
    /// host cleanup commit so it never lands in a PR. A no-op when untracked.
    /// </summary>
    public async Task<ProcResult> UntrackFleetConfigAsync(string worktree, CancellationToken ct = default)
    {
        var tracked = await Proc.RunAsync("git", $"ls-files --error-unmatch {FleetConfigFileName}", worktree, ct);
        if (!tracked.Ok) return tracked with { ExitCode = 0 }; // not tracked: fine

        await Proc.RunAsync("git", $"rm --cached {FleetConfigFileName}", worktree, ct);
        return await Proc.RunAsync("git",
            "commit -m \"chore: untrack fleet-local opencode config (host-owned)\"", worktree, ct);
    }

    public Task<ProcResult> PushAsync(string worktree, string branch, CancellationToken ct = default) =>
        Proc.RunAsync("git", $"push -u origin {branch}", worktree, ct);

    /// <summary>Remove a worktree (and its directory); safe to call twice.
    /// Runs from the repo's clone — git resolves the worktree through it.</summary>
    public Task<ProcResult> RemoveWorktreeAsync(string repo, string worktree, CancellationToken ct = default) =>
        Proc.RunAsync("git", $"worktree remove --force \"{worktree}\"", ClonePathFor(repo), ct);
}
