namespace AgentFactory.App.Fleet;

/// <summary>
/// Executes a parent run on a cross-repo ticket (ticket 020, ADR 0004): one
/// planning session turns the `.github` issue into ordered child specs, then
/// the parent — holding the fleet's single run slot — drives the children
/// sequentially as ordinary pipeline runs over synthetic issues (the planning
/// session's per-child prompts). Chained bases are literal where mechanics
/// allow: a child whose predecessor is on the SAME repo cuts from that
/// child's pushed branch, producing a true stacked PR; across repos a branch
/// cannot carry another repo's commits, so chaining there is the sequencing
/// plus the prior children's summaries carried into the next child's prompt.
/// Green children stand even when a later child fails terminally; that
/// failure cancels the remaining children and the parent ends failed-or-partial
/// with a summary. A parent re-trigger is a full fresh pass (fresh children,
/// fresh branches) by the label contract — nothing here re-runs subsets.
/// </summary>
public sealed class ParentExecutor(
    ILogger<ParentExecutor> logger,
    FleetOptions options,
    RunStore store,
    GitHubService github,
    EngineResolver engines,
    ModelHealthProbe health,
    OrgScanner orgs,
    RunExecutor executor,
    ConfigStore config)
{
    /// <summary>One planned child: the repo it lands in plus the prompt
    /// written for that repo's role in the ticket.</summary>
    public sealed record ChildSpec(string Repo, string Prompt);

    private string PlanDir => Path.Combine(options.DataDir, "parent-plan");

    public async Task ExecuteAsync(RunRecord parent, CancellationToken ct)
    {
        var org = parent.Repo.Split('/')[0];
        Directory.CreateDirectory(PlanDir);

        // 1. Planning session: the ticket (already carrying the original
        //    verbatim + triage repos) plus the org's eligible repos → ordered
        //    child specs. Recorded as the parent's single "plan" phase.
        var eligible = orgs.EligibleRepos(org);
        if (eligible.Count == 0)
        {
            Fail(parent, $"no eligible-repo snapshot for {org} — re-enroll or wait one scan cycle");
            return;
        }

        var planPrompt =
            $"""
            You are the Agent Factory agent-fleet's cross-repo planning agent. Your
            one job: turn the cross-repo ticket below into an ORDERED list of
            single-repo child tasks. You do not implement anything.

            THE TICKET, VERBATIM (on {parent.Repo}, org {org}):

            # {parent.IssueTitle}

            {parent.IssueBody}

            ---

            The org's eligible repos — children may target ONLY these:
            {string.Join("\n", eligible)}

            Rules:
            - Emit one child per repo-and-step that genuinely carries work; a
              small, correct plan beats a long one. Often fewer repos than the
              ticket names actually need changes — plan children only where
              work lands.
            - Order children so each builds on the previous ones: the repo
              others depend on goes first.
            - Each child's PROMPT is written for that repo's role in the
              ticket: what to change there, referencing the other repos'
              intended changes by NAME (e.g. "adapt to atlas renaming X to
              Y"), never pasting the whole ticket.

            Reply with EXACTLY this format, nothing before or after (n counts
            from 1; at least one child):
            CHILD 1: owner/name
            PROMPT:
            <the single-repo prompt for child 1, as it will be handed to that
            repo's implementing agent>
            CHILD 2: owner/name
            PROMPT:
            ...
            """;

        // The planning session runs on the fleet's utility profile + model when
        // set and usable (ticket 030); unusable pins fall back to engine
        // defaults, fail-soft like triage. The engine is the utility profile's
        // flavour adapter (ticket 032) or opencode defaults.
        string? utilityModel = null;
        string? utilityProfileName = null;
        string? utilityFlavourLabel = null;
        IAgentEngine engine = engines.Default;
        // One read of the binding: the store hot-reloads, so two reads could
        // pair one binding's profile with another's model (ticket 030).
        var utility = config.Utility();
        if (utility?.ProfileId is { Length: > 0 } utilityId
            && config.ProfileFor(utilityId) is { } utilityProfile
            && AgentFlavours.Executable(utilityProfile.Flavour))
        {
            if (ProfileCredential.Resolve(utilityProfile.ApiKey) is { Length: > 0 } utilityCredential
                && AgentFlavours.OpencodeProvider(utilityProfile.Flavour) is { } utilityProvider)
            {
                // The serve is the default engine's serve (every flavour
                // rides it), so the ensure goes through it directly —
                // before this plan's first session (ticket 042).
                await engines.Default.EnsureCredentialAsync(utilityProvider, utilityCredential, ct);
            }
            engine = engines.For(utilityProfile.Flavour);
            utilityModel = utility.ModelId;
            utilityProfileName = utilityProfile.Name;
            utilityFlavourLabel = AgentFlavours.Label(utilityProfile.Flavour);
        }

        // The utility model's health ladder (ticket 044, shared with triage):
        // a hung plan session is a parent-run orchestration error with no
        // retry, so a dead utility model re-shores onto the fleet fallback or
        // engine defaults — planning is fail-soft like triage.
        if (utilityModel is { Length: > 0 } probedUtilityModel)
        {
            var shore = await health.ResolveUtilityModelAsync(config, probedUtilityModel, ct);
            if (shore.Note.Length > 0) store.Log(parent, $"PLAN: {shore.Note}");
            utilityModel = shore.ModelId;
            if (shore.Profile is { } shoreProfile)
            {
                engine = engines.For(shoreProfile.Flavour);
                utilityProfileName = shoreProfile.Name;
                utilityFlavourLabel = AgentFlavours.Label(shoreProfile.Flavour);
            }
            else if (shore.ModelId is null)
            {
                engine = engines.Default;
                utilityProfileName = null;
                utilityFlavourLabel = null;
            }
        }

        var session = await engine.CreateSessionAsync(
            PlanDir, $"agent-factory parent-plan {parent.Repo}#{parent.IssueNumber}", utilityModel, ct);
        var result = await engine.RunPromptAsync(session, PlanDir, planPrompt, ct);
        parent.Phases =
        [
            new PhaseRecord
            {
                Name = "plan",
                SessionId = session,
                Cost = result.Cost,
                TokensIn = result.TokensIn,
                TokensOut = result.TokensOut,
                Summary = result.Text,
                FinishedAt = DateTimeOffset.UtcNow,
                ProfileName = utilityProfileName,
                Flavour = utilityFlavourLabel,
                ModelId = result.ModelId,
                ProviderId = result.ProviderId,
            },
        ];
        parent.SessionId = session;
        store.Update(parent);
        store.Log(parent, $"PLAN: session {session} (cost {result.Cost})" +
            (utilityProfileName is null
                ? ""
                : $" on utility pin {utilityProfileName} ({utilityFlavourLabel}) model {utilityModel ?? "(engine default)"}") +
            " — parsing child specs");

        var specs = ParsePlan(result.Text);
        var eligibleSet = eligible.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dropped = specs.Where(s => !eligibleSet.Contains(s.Repo)).ToList();
        specs = [.. specs.Where(s => eligibleSet.Contains(s.Repo))];
        if (dropped.Count > 0)
        {
            store.Log(parent, $"PLAN: dropped non-eligible repos named by the planner: {string.Join(", ", dropped.Select(s => s.Repo))}");
        }
        if (specs.Count == 0)
        {
            Fail(parent, $"planning session produced no eligible children (reply {result.Text.Length} chars)");
            return;
        }
        store.Log(parent, $"PLAN: {specs.Count} child(ren): {string.Join(" → ", specs.Select(s => s.Repo))}");

        // 2. Child runs: synthetic issues (the planner's per-child prompts),
        //    queued records for observability, driven inline by the parent —
        //    the fleet queue never dequeues them (Kind=Child).
        var children = new List<RunRecord>();
        for (var i = 0; i < specs.Count; i++)
        {
            var child = new RunRecord
            {
                Repo = specs[i].Repo,
                IssueNumber = 0, // synthetic: GitHub issues number from 1, so no real-issue collision
                IssueTitle = $"[child {i + 1}] {parent.IssueTitle} ({specs[i].Repo})",
                IssueBody = specs[i].Prompt,
                IssueUrl = parent.IssueUrl,
                Kind = RunKind.Child,
                ParentRunId = parent.RunId,
                ChildOrder = i,
                State = RunState.Queued,
            };
            child.LogPath = Path.Combine(options.DataDir, "runs", child.RunId + ".log");
            store.Add(child);
            store.Log(child, $"child {i + 1} of {specs.Count} planned by {parent.RunId} for {parent.IssueUrl}");
            children.Add(child);
        }

        // 3. Tracking comment, edited in place as children land.
        parent.TrackingCommentId = await github.CommentOnIssueTrackingAsync(
            parent.Repo, parent.IssueNumber, TrackingBody(parent, []), ct);
        store.Update(parent);

        // 4. Sequential execution with chaining where same-repo; context of
        //    prior children carried across repos.
        var priorBranch = (string?)null; // same-repo chain: previous child's pushed branch
        var priorRepo = (string?)null;
        var landed = new List<RunRecord>();
        foreach (var child in children)
        {
            if (ct.IsCancellationRequested) return;

            var chained = priorBranch is not null && child.Repo == priorRepo;
            var baseRef = chained ? priorBranch! : options.BaseBranch;
            child.StartedAt = DateTimeOffset.UtcNow;
            store.Update(child);
            if (!chained && landed.Count > 0)
            {
                // Cross-repo chaining is ordering + context (a branch cannot
                // carry another repo's commits): hand the child what its
                // predecessors did so it writes compatible changes.
                child.IssueBody = ChildBodyWithContext(specs[child.ChildOrder].Prompt, landed);
                store.Update(child);
            }
            store.Log(child, $"child {child.ChildOrder + 1} starting (base {baseRef})");
            logger.LogInformation("parent {Parent}: executing child {Run} on {Repo} (base {Base})",
                parent.RunId, child.RunId, child.Repo, baseRef);

            try
            {
                await executor.ExecuteAsync(child, ct, baseRef);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                child.State = RunState.Failed;
                child.Error = $"orchestration error: {ex.Message}";
                child.FinishedAt = DateTimeOffset.UtcNow;
                store.Update(child);
                store.Log(child, $"ORCHESTRATION ERROR: {ex}");
            }

            landed.Add(child);
            await UpdateTrackingAsync(parent, ct);

            if (child.State == RunState.Failed)
            {
                // Terminal child failure: downstream children's bases are
                // dead — cancel them; green children stand (ADR 0004).
                foreach (var downstream in children.Where(c => c.State == RunState.Queued))
                {
                    downstream.State = RunState.Cancelled;
                    downstream.Error = $"downstream of failed child {child.ChildOrder + 1} ({child.Repo})";
                    downstream.FinishedAt = DateTimeOffset.UtcNow;
                    store.Update(downstream);
                    store.Log(downstream, "CANCELLED: downstream of a terminally failed child");
                }
                await UpdateTrackingAsync(parent, ct);
                Fail(parent,
                    $"child {child.ChildOrder + 1} on {child.Repo} failed terminally — {landed.Count(c => c.State == RunState.Succeeded)} of {children.Count} child(ren) green, the rest cancelled. Green children's PRs stand.",
                    partial: true);
                return;
            }

            priorBranch = child.Branch; // pushed by the executor on success
            priorRepo = child.Repo;
        }

        parent.State = RunState.Succeeded;
        parent.FinishedAt = DateTimeOffset.UtcNow;
        store.Update(parent);
        store.Log(parent, "RUN SUCCEEDED: every child green");
        logger.LogInformation("parent {Parent} completed: all {Count} children green", parent.RunId, children.Count);
        await UpdateTrackingAsync(parent, ct, final: "All children green — merge in the listed order.");
    }

    private void Fail(RunRecord parent, string error, bool partial = false)
    {
        parent.State = RunState.Failed;
        parent.Error = (partial ? "partial: " : "") + error;
        parent.FinishedAt = DateTimeOffset.UtcNow;
        store.Update(parent);
        store.Log(parent, $"RUN FAILED{(partial ? " (PARTIAL)" : "")}: {error}");
        logger.LogInformation("parent {Parent} failed: {Error}", parent.RunId, error);
    }

    private async Task UpdateTrackingAsync(RunRecord parent, CancellationToken ct, string? final = null)
    {
        if (parent.TrackingCommentId is not { } id) return;
        var children = store.All.Where(r => r.ParentRunId == parent.RunId).OrderBy(r => r.ChildOrder).ToList();
        var updated = await github.UpdateCommentAsync(parent.Repo, id, TrackingBody(parent, children, final), ct);
        if (!updated.Ok)
        {
            store.Log(parent, $"tracking comment update failed: {updated.Output}");
        }
    }

    private static string TrackingBody(RunRecord parent, IReadOnlyList<RunRecord> children, string? final = null)
    {
        var rows = children.Count == 0
            ? "_children being planned…_"
            : string.Join("\n", children.Select(c =>
            {
                var outcome = c.State switch
                {
                    RunState.Succeeded => $"✅ [PR]({c.PrUrl})",
                    RunState.Failed => "❌ failed",
                    RunState.Cancelled => "⚪ cancelled",
                    RunState.Working => "🔄 running",
                    _ => "🕒 queued",
                };
                return $"| {c.ChildOrder + 1} | `{c.Repo}` | `{c.Branch ?? "—"}` | {outcome} |";
            }));
        var mergeOrder = children.Count == 0 ? "" :
            "\n\n**Merge order:** " + string.Join(" → ",
                children.Where(c => c.State is RunState.Succeeded or RunState.Working or RunState.Queued)
                    .OrderBy(c => c.ChildOrder)
                    .Select(c => c.Repo));

        return $"""
            **Agent Factory parent run** `{parent.RunId}` — cross-repo execution tracking (ticket 020).

            | # | repo | branch | outcome |
            |---|------|--------|---------|
            {rows}
            {mergeOrder}
            {(final is null ? "" : "\n" + final)}
            """;
    }

    /// <summary>Cross-repo context: the next child's prompt plus what its
    /// predecessors actually did (branch, PR, agent summary) so it writes
    /// compatible changes without access to their unmerged commits.</summary>
    private static string ChildBodyWithContext(string prompt, IReadOnlyList<RunRecord> landed)
    {
        var prior = string.Join("\n\n", landed.Select(c =>
            $"""
            ### Predecessor child {c.ChildOrder + 1} on `{c.Repo}`
            - Branch `{c.Branch}` → {c.PrUrl} (unmerged — do NOT expect its changes in your repo)
            - What it did: {(c.AgentSummary is { Length: > 0 } s ? s[..Math.Min(600, s.Length)] : "see its PR")}
            """));
        return $"""
            {prompt}

            ---

            CONTEXT — predecessor children of this cross-repo ticket (their
            changes are on unmerged branches in OTHER repos; coordinate by
            name, do not expect their code locally):

            {prior}
            """;
    }

    /// <summary>Strict-format parse of the planner's reply into ordered
    /// specs. Tolerant of blank lines inside prompts; a CHILD line without a
    /// PROMPT block is dropped.</summary>
    internal static List<ChildSpec> ParsePlan(string reply)
    {
        var specs = new List<ChildSpec>();
        string? repo = null;
        var prompt = new List<string>();
        foreach (var raw in reply.Split('\n'))
        {
            var line = raw.TrimEnd();
            var childMatch = line.TrimStart().StartsWith("CHILD ", StringComparison.OrdinalIgnoreCase);
            var promptMatch = line.TrimStart().StartsWith("PROMPT:", StringComparison.OrdinalIgnoreCase);
            if (childMatch && int.TryParse(line.TrimStart()["CHILD ".Length..].Split(':', 2)[0].Trim(), out _))
            {
                if (repo is { } r && prompt.Count > 0) specs.Add(new ChildSpec(r, string.Join('\n', prompt).Trim()));
                var named = line.Split(':', 2);
                repo = named.Length == 2 ? named[1].Trim() : null;
                prompt = [];
            }
            else if (promptMatch)
            {
                prompt = [line.TrimStart()["PROMPT:".Length..].Trim()];
            }
            else if (repo is not null)
            {
                prompt.Add(line);
            }
        }
        if (repo is { } last && prompt.Count > 0) specs.Add(new ChildSpec(last, string.Join('\n', prompt).Trim()));
        return specs.Where(s => s.Repo is { Length: > 0 } && s.Prompt.Length > 0).ToList();
    }
}
