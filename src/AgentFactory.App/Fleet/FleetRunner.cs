namespace AgentFactory.App.Fleet;

/// <summary>
/// The fleet engine's main loop (ticket 018): keep the opencode server up,
/// scan enrolled orgs into per-repo targets (<see cref="OrgScanner"/>), poll
/// every enabled target for intake-eligible issues over conditional GETs
/// (304 = nothing new, rate-limit-free), and execute runs round-robin across
/// targets at MaxConcurrentRuns (the MVP posture is 1) — the target that has
/// waited longest since its last started run goes next, so one org's backlog
/// can't starve the fleet. The fleet pause and per-target disable stop
/// dequeueing here; un-enrollment cancels queued runs (in-flight completes).
/// </summary>
public sealed class FleetRunner(
    ILogger<FleetRunner> logger,
    FleetOptions options,
    ConfigStore config,
    RunStore store,
    GitHubService github,
    OpenCodeEngine engine,
    RunExecutor executor,
    OrgScanner orgs,
    TriageService triage,
    CrossRepoRouter router,
    ParentExecutor parents,
    FleetStatusService status) : BackgroundService
{
    private readonly FleetOptions _options = options;

    // repo + "/label" | "/all" -> etag of the last 200; a 304 short-circuits the poll.
    private readonly Dictionary<string, string> _etags = [];
    private readonly HashSet<string> _labelEnsured = [];

    // The UI (targets enrollment/un-enrollment, ticket 022) nudges the loop so
    // a fresh enrollment's first scan fires immediately instead of waiting out
    // the poll interval. Swapped-then-set: waiters see it once, next cycle arms
    // a fresh one.
    private volatile TaskCompletionSource _wakeTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Run a poll/scan cycle now (used by the Targets page).</summary>
    public void Wake()
    {
        var tcs = _wakeTcs;
        _wakeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.TrySetResult();
    }

    /// <summary>Delay for the poll interval, cut short by <see cref="Wake"/>.</summary>
    private async Task DelayOrWakeAsync(TimeSpan delay, CancellationToken ct)
    {
        var wake = _wakeTcs.Task;
        await Task.WhenAny(Task.Delay(delay, ct), wake);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var targets = config.Targets();
        var orgList = config.Orgs();
        logger.LogInformation("Fleet engine starting: {Targets} enrolled target(s) [{Repos}], {Orgs} org(s) [{OrgNames}], poll {Poll}s, org re-list {Rescan} min, concurrency {Max}",
            targets.Count, string.Join(", ", targets.Select(t => t.Repo)),
            orgList.Count, string.Join(", ", orgList.Select(o => o.Org)),
            _options.PollIntervalSeconds, _options.OrgRescanMinutes, _options.MaxConcurrentRuns);

        try
        {
            try
            {
                await engine.StartAsync(stoppingToken);
                logger.LogInformation("engine up: opencode server on port {Port}", _options.ServerPort);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The board is still readable when the engine cannot boot, and
                // the operator gets the reason on it rather than a dead process.
                logger.LogError(ex, "engine did not start; the board stays available and the queue waits: {Message}", ex.Message);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await orgs.ScanAsync(stoppingToken);
                await PollAndEnqueueAsync(stoppingToken);
                CancelUnenrolledQueued();
                await DrainQueueAsync(stoppingToken);
                await DelayOrWakeAsync(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
            }

            logger.LogInformation("Fleet main loop exited (cancellation: {Cancelled})", stoppingToken.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Fleet engine stopping");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fleet engine crashed: {Message}", ex.Message);
            throw;
        }
    }

    /// <summary>Poll every enabled target for its intake rule, plus the
    /// `.github` routing inboxes of enabled orgs (parent-run intake, ticket
    /// 019); disarm label removals per repo (repos that 304'd are left alone
    /// — their labelled set is unchanged, not unknown).</summary>
    private async Task PollAndEnqueueAsync(CancellationToken ct)
    {
        // Fresh snapshot each cycle: targets enroll/un-enroll via the store.
        var targets = config.Targets().Where(t => t.Enabled).ToList();
        logger.LogDebug("poll cycle: {Count} enabled target(s)", targets.Count);
        var labelledByRepo = new Dictionary<string, HashSet<int>>();
        foreach (var target in targets)
        {
            try
            {
                if (target.Intake == IntakeRules.All)
                {
                    await PollAllIntakeAsync(target, ct);
                }
                else
                {
                    var labelled = await PollLabelIntakeAsync(target, ct);
                    if (labelled is not null) labelledByRepo[target.Repo] = labelled;
                }
                status.RecordPoll(target.Repo);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "poll of {Repo} failed: {Message}", target.Repo, ex.Message);
                status.RecordPoll(target.Repo, ex.Message);
            }
        }

        foreach (var org in config.Orgs().Where(o => o.Enabled))
        {
            try
            {
                var labelled = await PollCrossRepoIntakeAsync(org.Org, ct);
                if (labelled is not null) labelledByRepo[$"{org.Org}/.github"] = labelled;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "cross-repo intake poll of {Org}/.github failed: {Message}", org.Org, ex.Message);
            }
        }

        store.DisarmMissing(labelledByRepo);
    }

    /// <summary>Labelled issues on an enrolled org's `.github` are cross-repo
    /// tickets (ticket 014/019): they enqueue as parent-kind runs, left
    /// queued for ticket 020's executor. An org without a `.github` yet (none
    /// routed ever) is a quiet no-op, not a poll failure.</summary>
    private async Task<HashSet<int>?> PollCrossRepoIntakeAsync(string org, CancellationToken ct)
    {
        var inbox = $"{org}/.github";
        if (!await github.RepoExistsAsync(inbox, ct))
        {
            logger.LogDebug("{Inbox}: absent — no cross-repo intake yet", inbox);
            return null;
        }

        _etags.TryGetValue(inbox + "/label", out var etag);
        var (get, issues) = await github.ListLabelledIssuesConditionalAsync(inbox, etag, ct);
        if (get.NotModified) return null;
        if (get.Etag is { } fresh) _etags[inbox + "/label"] = fresh;

        foreach (var issue in issues)
        {
            if (!store.NeedsRun(inbox, issue.Number)) continue;
            var run = new RunRecord
            {
                Repo = inbox,
                IssueNumber = issue.Number,
                IssueTitle = issue.Title,
                IssueBody = issue.Body,
                IssueUrl = issue.Url,
                Kind = RunKind.Parent, // awaits ticket 020's executor
                State = RunState.Queued,
            };
            run.LogPath = Path.Combine(_options.DataDir, "runs", run.RunId + ".log");
            store.Add(run);
            store.Log(run, $"enqueued from {issue.Url} as a cross-repo ticket (parent run)");
            logger.LogInformation("enqueued parent run {Run} for {Inbox}#{Number}: {Title}",
                run.RunId, inbox, issue.Number, issue.Title);
        }
        return [.. issues.Select(i => i.Number)];
    }

    /// <summary>Label-intake poll: conditional GET on the labelled set. A 304
    /// returns null — the set is unchanged, so this cycle neither enqueues
    /// nor disarms for the repo. A 200 re-reads the set: label adds enqueue,
    /// label removals disarm (the remove/re-add re-trigger contract).</summary>
    private async Task<HashSet<int>?> PollLabelIntakeAsync(TargetRecord target, CancellationToken ct)
    {
        await EnsureLabelAsync(target, ct);
        _etags.TryGetValue(target.Repo + "/label", out var etag);
        var (get, issues) = await github.ListLabelledIssuesConditionalAsync(target.Repo, etag, ct);
        if (get.NotModified)
        {
            logger.LogDebug("{Repo}: 304 — labelled set unchanged", target.Repo);
            return null;
        }
        if (get.Etag is { } fresh) _etags[target.Repo + "/label"] = fresh;

        foreach (var issue in issues)
        {
            EnqueueIfNeeded(target, issue);
        }
        return [.. issues.Select(i => i.Number)];
    }

    /// <summary>`all`-intake poll (ADR 0003): open issues oldest-first, held
    /// under a cap of not-yet-started runs so an enrollment backlog can't
    /// flood the fleet — the remainder flows in as capacity frees. Runs
    /// completing don't change GitHub's representation (issues stay open
    /// until the human merges), so the drain loop drops this repo's etag on
    /// completion to force the top-up refetch.</summary>
    private async Task PollAllIntakeAsync(TargetRecord target, CancellationToken ct)
    {
        _etags.TryGetValue(target.Repo + "/all", out var etag);
        var (get, issues) = await github.ListOpenIssuesOldestFirstConditionalAsync(target.Repo, etag, ct);
        if (get.NotModified)
        {
            logger.LogDebug("{Repo}: 304 — open-issue set unchanged", target.Repo);
            return;
        }
        if (get.Etag is { } fresh) _etags[target.Repo + "/all"] = fresh;

        var outstanding = store.All.Count(r => r.Repo == target.Repo && r.State == RunState.Queued);
        foreach (var issue in issues) // oldest first
        {
            if (outstanding >= _options.AllIntakeBacklogCap) break;
            if (!store.NeedsRun(target.Repo, issue.Number)) continue;
            EnqueueIfNeeded(target, issue);
            outstanding++;
        }
    }

    /// <summary>Provision the opt-in label once per target (org-discovered
    /// repos arrive at runtime, so this can't live at startup); a failure
    /// retries next cycle.</summary>
    private async Task EnsureLabelAsync(TargetRecord target, CancellationToken ct)
    {
        if (_labelEnsured.Contains(target.Repo)) return;
        var label = await github.EnsureOptInLabelExists(target.Repo, ct);
        if (label.Ok)
        {
            _labelEnsured.Add(target.Repo);
        }
        else
        {
            logger.LogWarning("label ensure on {Repo} failed (retrying next cycle): {Output}", target.Repo, label.Output);
        }
    }

    private void EnqueueIfNeeded(TargetRecord target, IssueSnapshot issue)
    {
        if (!store.NeedsRun(target.Repo, issue.Number)) return;
        var run = new RunRecord
        {
            Repo = target.Repo,
            IssueNumber = issue.Number,
            IssueTitle = issue.Title,
            IssueBody = issue.Body,
            IssueUrl = issue.Url,
            State = RunState.Queued,
        };
        run.LogPath = Path.Combine(_options.DataDir, "runs", run.RunId + ".log");
        store.Add(run);
        store.Log(run, $"enqueued from {issue.Url} (updatedAt {issue.UpdatedAt:u})");
        logger.LogInformation("enqueued run {Run} for {Repo}#{Number}: {Title}",
            run.RunId, target.Repo, issue.Number, issue.Title);
    }

    /// <summary>Un-enrollment reconcile (ticket 013): queued runs of repos no
    /// longer enrolled are cancelled; a parent run is cancelled when its org
    /// is no longer enrolled (its repo — the org's `.github` inbox — is never
    /// a target). In-flight runs were snapshotted at start and finish on
    /// their own; history is kept — the dashboard shows the former target's
    /// repo.</summary>
    private void CancelUnenrolledQueued()
    {
        var enrolled = config.Targets().Select(t => t.Repo).ToHashSet();
        var orgs = config.Orgs().Select(o => o.Org).ToHashSet();
        var terminal = new[] { RunState.Succeeded, RunState.Failed, RunState.Routed, RunState.Cancelled, RunState.Disarmed };
        foreach (var run in store.All.Where(r => r.State == RunState.Queued))
        {
            var gone = run.Kind switch
            {
                // Parent: its org's routing inbox un-enrolled.
                RunKind.Parent => !orgs.Contains(run.Repo.Split('/')[0]),
                // Child: its repo's target un-enrolled, or its parent already
                // ended (crash-orphaned mid-plan) with no one left to drive it.
                RunKind.Child => !enrolled.Contains(run.Repo)
                    || store.All.FirstOrDefault(p => p.RunId == run.ParentRunId) is not { } parent
                    || terminal.Contains(parent.State),
                _ => !enrolled.Contains(run.Repo),
            };
            if (!gone) continue;
            run.State = RunState.Cancelled;
            run.Error = run.Kind == RunKind.Child ? "child orphaned before start" : "target un-enrolled before start";
            run.FinishedAt = DateTimeOffset.UtcNow;
            store.Update(run);
            store.Log(run, "CANCELLED: " + run.Error);
            logger.LogInformation("cancelled queued run {Run} for {Repo}#{Number}: {Reason}",
                run.RunId, run.Repo, run.IssueNumber, run.Error);
        }
    }

    /// <summary>Execute queued runs one at a time, round-robin across
    /// targets: next goes to the target that has waited longest since its
    /// last started run (never started = longest wait), ties to the oldest
    /// queued run. The fleet pause and per-target disable stop dequeueing —
    /// an in-flight run completes.</summary>
    private async Task DrainQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var next = PickNextRun();
            if (next is null) return;

            if (!engine.IsRunning)
            {
                try
                {
                    logger.LogWarning("opencode server not running; restarting");
                    await engine.StartAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "opencode server could not be restarted; queued work waits");
                    return;
                }
            }

            next.StartedAt = DateTimeOffset.UtcNow;
            store.Update(next);
            logger.LogInformation("executing {Run} (issue #{Number} on {Repo})", next.RunId, next.IssueNumber, next.Repo);
            try
            {
                // Parent runs (ticket 020): planning session + sequentially
                // driven children, all inside this run slot (concurrency 1
                // globally). Ordinary org-target runs pass the triage gate;
                // repo-only targets go straight to the pipeline.
                if (next.Kind == RunKind.Parent)
                {
                    await parents.ExecuteAsync(next, ct);
                }
                else
                {
                    var target = config.TargetFor(next.Repo);
                    if (target is { Org: { } org })
                    {
                        var outcome = await triage.TriageAsync(next, target, ct);
                        next.TriageVerdict = outcome.Route ? "cross" : "single";
                        next.TriageRepos = outcome.Route ? outcome.Repos : null;
                        next.TriageSummary = outcome.Summary;
                        store.Update(next);
                        store.Log(next, $"TRIAGE {next.TriageVerdict}: {outcome.Summary}");

                        if (outcome.Route)
                        {
                            next.RoutedIssueUrl = await router.RouteAsync(next, org, outcome.Repos, ct);
                            next.State = RunState.Routed;
                            next.FinishedAt = DateTimeOffset.UtcNow;
                            store.Update(next);
                            store.Log(next, $"RUN ROUTED: {next.RoutedIssueUrl}");
                            logger.LogInformation("run {Run} routed to {Url}", next.RunId, next.RoutedIssueUrl);
                            _etags.Remove(next.Repo + "/all");
                            continue;
                        }
                    }

                    await executor.ExecuteAsync(next, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Executor handles attempt-level failures; this is an
                // orchestration bug (triage/routing included).
                next.State = RunState.Failed;
                next.Error = $"orchestration error: {ex.Message}";
                next.FinishedAt = DateTimeOffset.UtcNow;
                store.Update(next);
                store.Log(next, $"ORCHESTRATION ERROR: {ex}");
                logger.LogError(ex, "pipeline error in {Run}", next.RunId);
            }

            // Capacity freed — refetch the all-intake list next poll so
            // held-back backlog can flow in (see PollAllIntakeAsync).
            _etags.Remove(next.Repo + "/all");
        }
    }

    private RunRecord? PickNextRun()
    {
        if (config.IsPaused()) return null;
        var enabledRepos = config.Targets().Where(t => t.Enabled).Select(t => t.Repo).ToHashSet();
        var orgs = config.Orgs().Where(o => o.Enabled).Select(o => $"{o.Org}/.github").ToHashSet();
        var runs = store.All;
        var lastStarted = runs.Where(r => r.StartedAt is not null)
            .GroupBy(r => r.Repo)
            .ToDictionary(g => g.Key, g => g.Max(r => r.StartedAt)!.Value);
        return runs
            // Ordinary runs dequeue on their target; parent runs on their
            // org's routing inbox; children never dequeue — their parent
            // drives them inline (ticket 020).
            .Where(r => r.State == RunState.Queued
                && r.Kind != RunKind.Child
                && (r.Kind == RunKind.Normal ? enabledRepos.Contains(r.Repo) : orgs.Contains(r.Repo)))
            .GroupBy(r => r.Repo)
            .Select(g => new { Repo = g.Key, LastStart = lastStarted.GetValueOrDefault(g.Key, DateTimeOffset.MinValue), Run = g.MinBy(r => r.CreatedAt)! })
            .OrderBy(x => x.LastStart)
            .ThenBy(x => x.Run.CreatedAt)
            .Select(x => x.Run)
            .FirstOrDefault();
    }
}
