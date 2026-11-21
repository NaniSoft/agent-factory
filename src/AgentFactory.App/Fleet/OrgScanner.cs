namespace AgentFactory.App.Fleet;

/// <summary>
/// The org half of the fleet scan loop (ticket 018): enrolled orgs are
/// re-listed on a TTL (~15 min) and the snapshot diffed by name — ordering is
/// not contractual (ticket 010) — into per-repo fleet targets. Eligibility per
/// ticket 013: skip archived repos, forks, repos with issues disabled, and the
/// org's <c>.github</c> (cross-repo intake, ticket 014); individually enrolled
/// repos win over the org (most-specific-wins — no double-enqueue). Org
/// settings materialize into a target at discovery; a repo that leaves the
/// eligible set (deleted, archived, …) is un-enrolled — cancelling queued runs
/// is the runner's reconcile, history stays on the dashboard.
/// </summary>
public sealed class OrgScanner(
    ILogger<OrgScanner> logger,
    FleetOptions options,
    ConfigStore config,
    GitHubService github)
{
    private readonly Dictionary<string, (DateTimeOffset ListedAt, HashSet<string> Eligible, List<OrgRepoSnapshot> Repos)> _snapshots = [];

    /// <summary>The org's last scan, for the Targets page (ticket 022): when
    /// it ran, and the full listing it saw (eligible + skipped detail derive
    /// from it). Null before the first scan this process.</summary>
    public sealed record OrgScanView(DateTimeOffset? ListedAt, IReadOnlyList<OrgRepoSnapshot> Repos);

    public OrgScanView Snapshot(string org) =>
        _snapshots.TryGetValue(org, out var snap)
            ? new OrgScanView(snap.ListedAt, snap.Repos)
            : new OrgScanView(null, []);

    public async Task ScanAsync(CancellationToken ct)
    {
        var orgs = config.Orgs();

        // An org removed from the store entirely un-enrolls its targets (its
        // snapshot would otherwise go stale without ever being rescanned);
        // a merely disabled org keeps its targets and just stops scanning.
        var enrolled = orgs.Select(o => o.Org).ToHashSet();
        foreach (var stale in _snapshots.Keys.Where(k => !enrolled.Contains(k)).ToList())
        {
            var managed = config.Targets().Where(t => t.Org == stale).Select(t => t.Repo).ToList();
            if (managed.Count > 0)
            {
                config.RemoveTargets(managed);
                foreach (var repo in managed)
                {
                    logger.LogInformation("org {Org} un-enrolled: removed target {Repo}", stale, repo);
                }
            }
            _snapshots.Remove(stale);
        }

        var ttl = TimeSpan.FromMinutes(options.OrgRescanMinutes);
        foreach (var org in orgs.Where(o => o.Enabled))
        {
            if (_snapshots.TryGetValue(org.Org, out var snap)
                && DateTimeOffset.UtcNow - snap.ListedAt < ttl) continue;
            try
            {
                await ScanOrgAsync(org, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "org scan of {Org} failed: {Message}", org.Org, ex.Message);
            }
        }
    }

    /// <summary>The org's eligible-repo names from the last snapshot (empty
    /// when the org hasn't been scanned yet) — what triage may name in a
    /// cross verdict, and what the `.github` intake checks ownership against
    /// (ticket 019).</summary>
    public IReadOnlyList<string> EligibleRepos(string org) =>
        _snapshots.TryGetValue(org, out var snap) ? [.. snap.Eligible] : [];

    private async Task ScanOrgAsync(OrgRecord org, CancellationToken ct)
    {
        var repos = await github.ListOrgReposAsync(org.Org, ct);
        var eligible = repos.Where(r => IsEligible(org.Org, r)).Select(r => r.NameWithOwner).ToHashSet();

        var targets = config.Targets();
        var managed = targets.Where(t => t.Org == org.Org).Select(t => t.Repo).ToHashSet();
        var claimed = targets.Select(t => t.Repo).ToHashSet(); // individual enrollments win

        foreach (var repo in eligible.Except(claimed).OrderBy(r => r))
        {
            config.AddTarget(new TargetRecord
            {
                Repo = repo,
                Org = org.Org,
                Intake = org.Intake,
                PipelineId = org.PipelineId,
                Enabled = true,
                DefinitionOfDone = org.DefinitionOfDone,
            });
            logger.LogInformation("org {Org}: enrolled {Repo} (intake {Intake}, dod \"{Dod}\")",
                org.Org, repo, org.Intake, org.DefinitionOfDone);
        }

        var removed = managed.Except(eligible).OrderBy(r => r).ToList();
        if (removed.Count > 0)
        {
            config.RemoveTargets(removed);
            foreach (var repo in removed)
            {
                logger.LogInformation("org {Org}: un-enrolled {Repo} (left the eligible set)", org.Org, repo);
            }
        }

        _snapshots[org.Org] = (DateTimeOffset.UtcNow, eligible, repos);
        logger.LogInformation("org {Org}: scan complete — {Eligible} eligible of {Total} repos, {New} newly enrolled, {Removed} un-enrolled (next re-list in {Ttl} min)",
            org.Org, eligible.Count, repos.Count, eligible.Except(claimed).Count(), removed.Count, options.OrgRescanMinutes);
    }

    private static bool IsEligible(string org, OrgRepoSnapshot repo) =>
        repo.NameWithOwner != $"{org}/.github"        // cross-repo intake (ticket 014)
        && !repo.IsArchived && !repo.IsFork && repo.HasIssuesEnabled;
}
