using System.Text.Json;

namespace AgentFactory.App.Fleet;

/// <summary>
/// Executes one run end-to-end (ticket 016): worktree → run-start fail-fast →
/// phase walk — one engine session per phase, each with its own rendered
/// template and skill map in the per-worktree <c>opencode.json</c> — commit
/// check → host-owned push + PR. Everything repo-shaped is keyed by the run's
/// fleet target (ticket 017): clone and worktree under &lt;owner&gt;__&lt;repo&gt;,
/// the target's pipeline assignment, and its definition-of-done. Retry is per
/// run: a full pipeline pass on a fresh worktree, retry-once (v1 contract).
/// </summary>
public sealed class RunExecutor(
    FleetOptions options,
    ConfigStore config,
    GitService git,
    GitHubService github,
    EngineResolver engines,
    ModelHealthProbe healthProbe,
    RunStore store)
{
    /// <summary>Base ref this run's branch is cut from: the fleet base branch
    /// for ordinary runs and first children; a chained child cuts from the
    /// previous child's pushed branch (same-repo chains, ticket 020).</summary>
    public async Task ExecuteAsync(RunRecord run, CancellationToken ct, string? baseRef = null)
    {
        baseRef ??= options.BaseBranch;
        // Snapshot at start (ticket 015): in-flight runs are immune to config
        // edits; queued runs pick them up when they start. The target snapshot
        // carries the run's pipeline assignment and definition of done.
        var target = config.TargetFor(run.Repo);
        if (target is null)
        {
            run.State = RunState.Failed;
            run.Error = $"target {run.Repo} is no longer enrolled";
            run.FinishedAt = DateTimeOffset.UtcNow;
            store.Update(run);
            store.Log(run, $"RUN FAILED: target {run.Repo} un-enrolled before start");
            return;
        }
        if (!target.Enabled)
        {
            // The queue never offers disabled targets, but a parent's child
            // bypasses the queue (ticket 020) — fail fast rather than run
            // work on a target the operator switched off.
            run.State = RunState.Failed;
            run.Error = $"target {run.Repo} is disabled";
            run.FinishedAt = DateTimeOffset.UtcNow;
            store.Update(run);
            store.Log(run, $"RUN FAILED: target {run.Repo} disabled before start");
            return;
        }
        var pipeline = config.PipelineFor(target);
        run.PipelineName = pipeline.Name;
        // Profile snapshot (ticket 030): phase pins resolve against the store
        // once per run, next to the pipeline snapshot — in-flight runs are
        // immune to profile edits. A missing profile stays null here and fails
        // fast below; it never silently falls back to engine defaults.
        var profiles = pipeline.Phases.Where(p => p.ProfileId is { Length: > 0 })
            .Select(p => p.ProfileId!).Distinct()
            .ToDictionary(id => id, id => config.ProfileFor(id));

        // Fallback binding snapshot (ticket 044): resolved next to the
        // pipeline and profile snapshots — in-flight runs are immune to
        // config edits. Atomic like a pin (profile and model together); an
        // unusable binding logs its reason once and is treated as absent,
        // never as a silent pin.
        (AgentProfile Profile, string ModelId)? fallback = null;
        if (ModelPlanner.FallbackSnapshot(config) is { } fallbackSnapshot)
        {
            var (fallbackProfile, fallbackModel, unusableReason) = fallbackSnapshot;
            if (unusableReason is { } why)
            {
                store.Log(run, $"fallback binding {why} — runs proceed without one");
            }
            else
            {
                fallback = (fallbackProfile!, fallbackModel);
            }
        }

        var maxAttempts = 2;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            run.Attempt = attempt;
            run.State = RunState.Working;
            run.Phases = []; // a retry is a full fresh pipeline pass
            run.SessionId = null;
            store.Update(run);

            var outcome = await TryAttemptAsync(run, attempt, target, pipeline, profiles, fallback, baseRef, ct);
            if (outcome is { Succeeded: true })
            {
                run.PrUrl = outcome.PrUrl;
                run.State = RunState.Succeeded;
                run.FinishedAt = DateTimeOffset.UtcNow;
                store.Update(run);
                store.Log(run, $"RUN SUCCEEDED: {outcome.PrUrl}");
                return;
            }

            store.Log(run, $"attempt {attempt} failed: {outcome.Error}");
            run.Error = outcome.Error;
            if (attempt < maxAttempts)
            {
                store.Log(run, "retrying once (ticket 004: retry-once, full pipeline pass)");
            }
        }

        run.State = RunState.Failed;
        run.FinishedAt = DateTimeOffset.UtcNow;
        store.Update(run);

        // Child failures surface through the parent's summary comment on the
        // `.github` ticket (their synthetic issue exists nowhere to comment).
        if (run.Kind == RunKind.Child)
        {
            store.Log(run, "RUN FAILED after retry (child — parent summarizes on the .github issue)");
            return;
        }

        var logHint = $"Run log: `{run.LogPath}`.";
        var comment = await github.CommentOnIssue(
            run.Repo, run.IssueNumber, PromptBuilder.FailureComment(run, logHint), ct);
        store.Log(run, comment.Ok
            ? "failure comment posted on issue"
            : $"failed to comment on issue: {comment.Output}");
        store.Log(run, "RUN FAILED after retry");
    }

    private sealed record AttemptOutcome(bool Succeeded, string? Error, string? PrUrl);

    private async Task<AttemptOutcome> TryAttemptAsync(
        RunRecord run, int attempt, TargetRecord target, PipelineDefinition pipeline,
        Dictionary<string, AgentProfile?> profiles, (AgentProfile Profile, string ModelId)? fallback,
        string baseRef, CancellationToken ct)
    {
        string? worktree = null;
        var branch = git.BranchFor(run.IssueNumber, attempt, run.RunId);
        try
        {
            // 1. Fresh worktree off the base ref, in the repo's keyed subtree.
            //    The clone is ensured lazily here so a target enrolled while
            //    the engine is up still runs. A chained child cuts from the
            //    previous child's pushed branch (ticket 020).
            await git.EnsureCloneAsync(run.Repo, ct);
            var created = await git.CreateWorktreeAsync(run.Repo, run.IssueNumber, attempt, run.RunId, baseRef, ct);
            if (!created.Ok) return new AttemptOutcome(false, $"worktree create failed: {created.Output}", null);
            worktree = git.WorktreeFor(run.Repo, run.IssueNumber, attempt, run.RunId);
            run.WorktreePath = worktree;
            run.Branch = branch;
            store.Update(run);
            store.Log(run, $"worktree ready: {worktree} on {branch}" +
                (baseRef == options.BaseBranch ? "" : $" (chained base {baseRef})"));
            store.Log(run, $"target {run.Repo}: pipeline '{pipeline.Name}' " +
                $"({string.Join(" → ", pipeline.Phases.Select(p => p.Name))}), DoD `{target.DefinitionOfDone}`");

            // 2. Run-start fail-fast: every phase's skills must be discoverable
            //    by its own phase engine (the flavour's directory-scoped truth,
            //    library on disk as fallback — ticket 012; per-flavour engines
            //    since ticket 032) and every template must use only the six
            //    guaranteed placeholders (ticket 011).
            var engineCache = new Dictionary<AgentFlavour, IAgentEngine>();
            IAgentEngine EngineFor(AgentFlavour? flavour)
            {
                if (flavour is not { } f) return engines.Default;
                if (!engineCache.TryGetValue(f, out var resolved))
                    resolved = engineCache[f] = engines.For(f);
                return resolved;
            }
            var discoveredByEngine = new Dictionary<IAgentEngine, List<string>>();
            foreach (var phase in pipeline.Phases)
            {
                var phaseEngine = EngineFor(profiles.GetValueOrDefault(phase.ProfileId ?? "")?.Flavour);
                if (!discoveredByEngine.TryGetValue(phaseEngine, out var known))
                {
                    var discovered = await phaseEngine.ListSkillsAsync(worktree, ct);
                    known = discovered is { Count: > 0 } ? discovered : SkillLibrary.NamesOnDisk();
                    discoveredByEngine[phaseEngine] = known;
                    store.Log(run, discovered is { Count: > 0 }
                        ? $"{EngineName(phaseEngine)} discovers {discovered.Count} skills for this worktree"
                        : $"{EngineName(phaseEngine)}: no discovery — filesystem fallback: {known.Count} skills in the library");
                }
                var missing = phase.Skills.Where(s => !known.Contains(s, StringComparer.Ordinal)).ToList();
                if (missing.Count > 0)
                    return new AttemptOutcome(false,
                        $"phase '{phase.Name}' references missing skill(s): {string.Join(", ", missing)}", null);
                var unknown = Placeholders.Unknown(phase.Template);
                if (unknown.Count > 0)
                    return new AttemptOutcome(false,
                        $"phase '{phase.Name}' template uses unknown placeholder(s): " +
                        string.Join(", ", unknown.Select(u => "{{" + u + "}}")) +
                        " — allowed: " + string.Join(", ", Placeholders.All.Select(a => "{{" + a + "}}")), null);

                // Pin validation (ticket 030): pins are atomic (028) — profile
                // and model together or neither; the model must be in the
                // profile's palette; the flavour must have an adapter today.
                if (phase.ProfileId is { Length: > 0 } pinId)
                {
                    var profile = profiles.GetValueOrDefault(pinId);
                    if (profile is null)
                        return new AttemptOutcome(false,
                            $"phase '{phase.Name}' pins profile '{pinId}' which no longer exists", null);
                    if (phase.ModelId is not { Length: > 0 })
                        return new AttemptOutcome(false,
                            $"phase '{phase.Name}' pins profile '{profile.Name}' without a model — " +
                            "pins are atomic: profile and model, or neither (ticket 028)", null);
                    if (!profile.Models.Contains(phase.ModelId))
                        return new AttemptOutcome(false,
                            $"phase '{phase.Name}' pins '{phase.ModelId}' which is not in " +
                            $"profile '{profile.Name}'s palette", null);
                    if (!AgentFlavours.Executable(profile.Flavour))
                        return new AttemptOutcome(false,
                            $"phase '{phase.Name}' pins flavour {AgentFlavours.Label(profile.Flavour)} " +
                            "which has no execution adapter yet (configurable only)",
                            null);
                }
            }

            // Profile credentials reach the serve per tickets 027/042 — the
            // ensure path: PUT /auth/{provider} with the shape-sniffed
            // payload (api key vs oauth envelope), and a serve restart when
            // the running process didn't boot with it (auth is boot-time).
            // {env:VAR} references resolve first; missing vars resolve empty
            // and the credential is skipped (native login wins, ticket 025).
            // First pinned profile per provider wins; a same-provider
            // conflict logs, and is not fatal. This runs before the first
            // session of the run, so an ensure restart cannot kill in-flight
            // work (fleet concurrency is 1).
            var injected = new HashSet<string>();
            foreach (var phase in pipeline.Phases)
            {
                if (phase.ProfileId is not { Length: > 0 } injectId) continue;
                var profile = profiles.GetValueOrDefault(injectId);
                if (profile is null) continue;
                var credential = ProfileCredential.Resolve(profile.ApiKey);
                if (credential.Length == 0) continue;
                var provider = AgentFlavours.OpencodeProvider(profile.Flavour);
                if (provider is null) continue;
                if (!injected.Add(provider))
                {
                    store.Log(run, $"auth: provider {provider} already has a credential — " +
                        $"profile '{profile.Name}'s skipped (first pin wins)");
                    continue;
                }
                await engines.Default.EnsureCredentialAsync(provider, credential, ct);
                store.Log(run, $"auth: credential for provider {provider} ensured from profile '{profile.Name}'");
            }

            // Model health (ticket 044): every model this attempt may run is
            // probe-checked before the walk — each distinct pinned model, and
            // the fallback whenever it is configured (it is a candidate for
            // both unpinned phases and dead-pin substitution). The fallback's
            // credential ensure (possible bake restart) and its probe both
            // land here, before the walk's first session, so neither can kill
            // in-flight work. Probes cache per model for a TTL window, so a
            // run's several phases and its retry share one verdict.
            await engines.Default.StartAsync(ct);
            var health = new Dictionary<string, ModelHealthProbe.Verdict>();
            foreach (var modelId in pipeline.Phases
                .Where(p => p.ProfileId is { Length: > 0 })
                .Select(p => p.ModelId!)
                .Distinct())
            {
                health[modelId] = await healthProbe.ProbeAsync(modelId, ct);
            }
            if (fallback is { } fb)
            {
                var fallbackProvider = AgentFlavours.OpencodeProvider(fb.Profile.Flavour);
                var fallbackCredential = ProfileCredential.Resolve(fb.Profile.ApiKey);
                if (fallbackProvider is { } && fallbackCredential.Length > 0 && !injected.Add(fallbackProvider))
                {
                    store.Log(run, $"auth: provider {fallbackProvider} already has a credential — " +
                        $"fallback profile's skipped (first pin wins)");
                }
                else if (fallbackProvider is { } && fallbackCredential.Length > 0)
                {
                    await engines.Default.EnsureCredentialAsync(fallbackProvider, fallbackCredential, ct);
                    store.Log(run, $"auth: credential for provider {fallbackProvider} ensured from fallback profile '{fb.Profile.Name}'");
                }
                health[fb.ModelId] = await healthProbe.ProbeAsync(fb.ModelId, ct);
            }

            // The per-phase decision (ticket 044): pin, fallback, or engine
            // defaults — pure over the verdicts just taken. A failing plan
            // (dead pin with no healthy fallback to land on) fails the
            // attempt here, where the retry costs seconds, not the session
            // timeout — 041's twin 30-minute triage timeouts become minutes.
            var plans = pipeline.Phases
                .Select(phase => ModelPlanner.Plan(
                    phase.ProfileId is { Length: > 0 } planPinId
                        ? profiles.GetValueOrDefault(planPinId) : null,
                    phase.ModelId, fallback, health))
                .ToList();
            if (plans.FirstOrDefault(p => p.FailReason is not null) is { FailReason: not null } failed)
            {
                return new AttemptOutcome(false, failed.FailReason, null);
            }

            var vars = new PromptVars(
                run.IssueTitle, run.IssueBody, run.Repo, branch,
                baseRef, target.DefinitionOfDone);

            // 3. Phase walk: one engine session per phase (ticket 011), each
            //    on the flavour its pin resolves to — the per-worktree
            //    opencode.json is rewritten so each session sees exactly its
            //    skill set (deny-all first, allows after — ticket 012).
            foreach (var (phase, plan) in pipeline.Phases.Zip(plans))
            {
                var phaseEngine = EngineFor(plan.Profile?.Flavour);
                var record = new PhaseRecord
                {
                    Name = phase.Name,
                    Skills = [.. phase.Skills],
                    ProfileName = plan.Profile?.Name,
                    Flavour = plan.Profile is null ? null : AgentFlavours.Label(plan.Profile.Flavour),
                };
                run.Phases.Add(record);
                WritePermissionConfig(worktree, phase);
                var phaseFacts = new List<string>
                {
                    $"skill map [{string.Join(", ", phase.Skills)}] written",
                };
                if (plan.Note.Length > 0)
                    phaseFacts.Add(plan.Note);
                phaseFacts.Add($"engine {EngineName(phaseEngine)}");
                store.Log(run, $"PHASE {phase.Name}: {string.Join("; ", phaseFacts)}; creating session");

                var session = await phaseEngine.CreateSessionAsync(
                    worktree, $"agent-factory issue-{run.IssueNumber} attempt-{attempt} {phase.Name}",
                    plan.ModelId, ct);
                record.SessionId = session;
                run.SessionId = session;
                store.Update(run);
                store.Log(run, $"PHASE {phase.Name}: session {session}; sending phase prompt");

                var result = await phaseEngine.RunPromptAsync(
                    session, worktree, Placeholders.Render(phase.Template, vars), ct);
                record.FinishedAt = DateTimeOffset.UtcNow;
                record.Cost = result.Cost;
                record.TokensIn = result.TokensIn;
                record.TokensOut = result.TokensOut;
                record.Summary = result.Text;
                // The engine's readback (GET /session model, ticket 027) is
                // truth over intent; a mismatch with the plan is a wart worth
                // logging.
                record.ModelId = result.ModelId ?? plan.ModelId;
                record.ProviderId = result.ProviderId
                    ?? (plan.Profile is null ? null : AgentFlavours.OpencodeProvider(plan.Profile.Flavour));
                // An authless provider fails SILENTLY (ticket 042: empty reply,
                // zero tokens, no error part — the run would sail on to fail
                // later at the commit check). Fail the attempt here where the
                // retry gets a fresh ensure (and its bake restart).
                if (result.Text.Length == 0 && result.TokensOut == 0)
                    return new AttemptOutcome(false,
                        $"phase '{phase.Name}' returned an empty reply with zero output tokens — " +
                        "its provider had no usable credential at serve boot (auth is boot-time, ticket 042)", null);
                if (plan.ModelId is { } wanted && result.ModelId is { } ran
                    && ran != wanted.Split('/')[^1] && ran != wanted)
                {
                    store.Log(run,
                        $"PHASE {phase.Name}: WARNING engine ran '{ran}' but the plan was '{wanted}'");
                }
                store.Update(run);
                store.Log(run,
                    $"PHASE {phase.Name}: session finished (cost {result.Cost}, " +
                    $"{result.TokensIn}in/{result.TokensOut}out" +
                    (record.ModelId is { Length: > 0 } ranModel ? $", model {ranModel}" : "") +
                    $"); final reply {result.Text.Length} chars");
            }
            run.AgentSummary = run.Phases.LastOrDefault()?.Summary;
            store.Update(run);

            // 4. The phases must have committed work on the branch.
            var commits = await git.CountCommitsAsync(worktree, branch, baseRef, ct);
            if (commits == 0)
                return new AttemptOutcome(false, "pipeline finished but committed nothing on " + branch, null);
            var changed = await git.ChangedFilesAsync(worktree, baseRef, ct);
            store.Log(run, $"branch {branch} carries {commits} commit(s) on top of {baseRef}: {string.Join(", ", changed)}");

            // 5. Host-owned flow (ADR 0001): strip fleet config if tracked, push, open PR.
            var untracked = await git.UntrackFleetConfigAsync(worktree, ct);
            if (!untracked.Ok)
                return new AttemptOutcome(false, "fleet-config cleanup failed: " + untracked.Output, null);

            var pushed = await git.PushAsync(worktree, branch, ct);
            if (!pushed.Ok) return new AttemptOutcome(false, $"push failed: {pushed.Output}", null);
            store.Log(run, $"pushed {branch}");

            var pr = await github.CreatePullRequest(
                run.Repo, branch,
                run.Kind == RunKind.Child ? run.IssueTitle : $"Implement #{run.IssueNumber}: {run.IssueTitle}",
                PromptBuilder.PrBody(run), ct);
            if (!pr.Ok) return new AttemptOutcome(false, $"gh pr create failed: {pr.Output}", null);

            var prUrl = pr.Output.Split('\n')[0].Trim();
            await git.RemoveWorktreeAsync(run.Repo, worktree, ct);
            return new AttemptOutcome(true, null, prUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AttemptOutcome(false, $"{ex.GetType().Name}: {ex.Message}", null);
        }
    }

    /// <summary>The run log's name for a phase engine — flavour wording, not
    /// class wording (the glossary's Engine entry).</summary>
    private static string EngineName(IAgentEngine engine) => engine switch
    {
        OpenCodeEngine => "opencode-serve",
        _ => engine.GetType().Name,
    };

    /// <summary>Per-worktree permission config (ADR 0002 + ticket 012): edit+bash
    /// allowed, skill deny-all first then exactly this phase's skills (last
    /// match wins — the map is what carves the phase's skill set).</summary>
    private static void WritePermissionConfig(string worktree, PhaseDefinition phase)
    {
        var skillMap = new Dictionary<string, string> { ["*"] = "deny" };
        foreach (var skill in phase.Skills) skillMap[skill] = "allow";

        var configPath = Path.Combine(worktree, GitService.FleetConfigFileName);
        File.WriteAllText(configPath, JsonSerializer.Serialize(new
        {
            permission = new { edit = "allow", bash = "allow", skill = skillMap },
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
