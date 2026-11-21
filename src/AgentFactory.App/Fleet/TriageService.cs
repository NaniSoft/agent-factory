namespace AgentFactory.App.Fleet;

/// <summary>
/// The intake triage verdict on org targets (ticket 019): is this issue
/// single-repo work, or a cross-repo ticket? Override labels settle it without
/// a session (`agent-factory:here` forces in-repo, `agent-factory:cross` forces
/// routing — label + re-trigger recovers from a wrong verdict, ticket 007
/// machinery); otherwise one cheap engine session reads the issue plus the
/// org's eligible repos and answers in a strict two-line format. Best-effort,
/// playbook-not-security (ticket 014): an unparsable or failed session falls
/// back to `single`, the safe default that keeps work in-repo. Repo-only
/// targets never reach here — no session is spent on them.
/// </summary>
public sealed class TriageService(
    ILogger<TriageService> logger,
    FleetOptions options,
    GitHubService github,
    EngineResolver engines,
    ModelHealthProbe health,
    OrgScanner orgs,
    ConfigStore config)
{
    public const string HereOverrideLabel = "agent-factory:here";
    public const string CrossOverrideLabel = "agent-factory:cross";

    /// <summary>Verdict plus provenance. Route=true means the caller routes
    /// (CrossRepoRouter) instead of executing; Repos is the filtered eligible
    /// list (nominal for a forced cross — the parent run's planning session,
    /// ticket 020, decides the real split).</summary>
    public sealed record TriageOutcome(bool Route, List<string> Repos, string Summary);

    private string TriageDir => Path.Combine(options.DataDir, "triage");

    public async Task<TriageOutcome> TriageAsync(RunRecord run, TargetRecord target, CancellationToken ct)
    {
        Directory.CreateDirectory(TriageDir);

        // 1. Override labels beat any session (ticket 014: overridable verdict).
        List<string> labels;
        try
        {
            labels = await github.GetIssueLabelsAsync(run.Repo, run.IssueNumber, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "label read on {Repo}#{Number} failed: {Message} — falling back to session triage",
                run.Repo, run.IssueNumber, ex.Message);
            labels = [];
        }
        if (labels.Contains(CrossOverrideLabel))
        {
            var nominal = orgs.EligibleRepos(target.Org!).Where(r => r != run.Repo).ToList();
            return new TriageOutcome(true, nominal, $"override label {CrossOverrideLabel} — routed without a triage session");
        }
        if (labels.Contains(HereOverrideLabel))
        {
            return new TriageOutcome(false, [], $"override label {HereOverrideLabel} — in-repo without a triage session");
        }

        // 2. One cheap session over the issue + the eligible repo list, on the
        //    fleet's utility profile + model when set (ticket 030). Unusable
        //    pins (profile gone, adapter-less flavour) fall back to engine
        //    defaults — triage is best-effort, and fail-soft beats failing the
        //    run over a stale binding.
        var eligible = orgs.EligibleRepos(target.Org!);
        if (eligible.Count == 0)
        {
            return new TriageOutcome(false, [], "no eligible-repo snapshot for the org — defaulted to in-repo");
        }

        string? utilityModel = null;
        string utilityNote = "";
        // One read of the binding: the store hot-reloads, so two reads could
        // pair one binding's profile with another's model (ticket 030).
        var utility = config.Utility();
        // The engine the session runs on: opencode defaults, or the utility
        // profile's flavour adapter (ticket 032) when the pin is usable.
        IAgentEngine engine = engines.Default;
        if (utility?.ProfileId is { Length: > 0 } utilityId
            && config.ProfileFor(utilityId) is { } utilityProfile)
        {
            if (!AgentFlavours.Executable(utilityProfile.Flavour))
            {
                utilityNote = "utility pin unusable (adapter-less flavour) — engine default used";
            }
            else
            {
                if (ProfileCredential.Resolve(utilityProfile.ApiKey) is { Length: > 0 } utilityCredential
                    && AgentFlavours.OpencodeProvider(utilityProfile.Flavour) is { } utilityProvider)
                {
                    // The serve is the default engine's serve (every flavour
                    // rides it), so the ensure goes through it directly —
                    // before this triage's first session (ticket 042).
                    await engines.Default.EnsureCredentialAsync(utilityProvider, utilityCredential, ct);
                }
                engine = engines.For(utilityProfile.Flavour);
                utilityModel = utility.ModelId;
                utilityNote = $"utility pin {utilityProfile.Name}/{utilityModel ?? "(engine default)"}";
            }
        }

        // The utility model's health ladder (ticket 044, shared with parent
        // planning): a dead upstream otherwise hangs the session into the
        // run-level timeout — an orchestration error with no retry, 041's
        // twin failures. The ladder re-shores a dead model onto the fleet
        // fallback or engine defaults; triage never fails the run over it.
        if (utilityModel is { Length: > 0 } probedUtilityModel)
        {
            var shore = await health.ResolveUtilityModelAsync(config, probedUtilityModel, ct);
            if (shore.Note.Length > 0) utilityNote = shore.Note;
            utilityModel = shore.ModelId;
            if (shore.Profile is { } shoreProfile)
                engine = engines.For(shoreProfile.Flavour);
            else if (shore.ModelId is null)
                engine = engines.Default;
        }

        var prompt =
            $"""
            You are the Agent Factory agent-fleet's cross-repo triage agent. Your
            one job: decide whether the GitHub issue below is single-repo work
            or work that spans multiple repos of its org. You do not implement
            anything — you read and classify.

            THE ISSUE, VERBATIM, on repo {run.Repo} (org {target.Org}):

            # {run.IssueTitle}

            {run.IssueBody}

            ---

            The org's eligible repos — a cross verdict may name ONLY these:
            {string.Join("\n", eligible)}

            Rules:
            - Most issues are single-repo work. Say cross only when the issue
              itself names, or clearly implies, changes in OTHER repos of the
              org beyond {run.Repo}.
            - A cross verdict names at least one repo other than {run.Repo}.
            - Do not run tools; do not read or edit anything. Just classify.

            Reply with EXACTLY this format, nothing before or after:
            VERDICT: single
            or
            VERDICT: cross
            REPOS: owner/name, owner/name
            """;

        var session = await engine.CreateSessionAsync(
            TriageDir, $"agent-factory triage {run.Repo}#{run.IssueNumber}", utilityModel, ct);
        var result = await engine.RunPromptAsync(session, TriageDir, prompt, ct);
        run.TriageSessionId = session;

        var (verdict, named) = ParseReply(result.Text);
        if (verdict != "cross" && verdict != "single")
        {
            return new TriageOutcome(false, [],
                $"triage session unparsable (reply {result.Text.Length} chars) — defaulted to in-repo{Suffix(utilityNote)}");
        }

        if (verdict == "single")
        {
            return new TriageOutcome(false, [],
                $"triage session: single (cost {result.Cost}, {result.TokensIn}in/{result.TokensOut}out){Suffix(utilityNote)}");
        }

        // Cross: keep only eligible names; flag the rest (ticket 019).
        var eligibleSet = eligible.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var repos = named.Where(eligibleSet.Contains).Distinct().ToList();
        var flagged = named.Where(n => !eligibleSet.Contains(n)).ToList();
        var source = run.Repo;
        if (repos.All(r => r.Equals(source, StringComparison.OrdinalIgnoreCase)) || repos.Count == 0)
        {
            return new TriageOutcome(false, [],
                $"triage session said cross but named no other eligible repo (flagged: {string.Join(", ", flagged)}) — kept in-repo{Suffix(utilityNote)}");
        }
        return new TriageOutcome(true, repos,
            $"triage session: cross (cost {result.Cost}, {result.TokensIn}in/{result.TokensOut}out)" +
            (flagged.Count > 0 ? $"; flagged non-eligible names dropped: {string.Join(", ", flagged)}" : "") +
            Suffix(utilityNote));
    }

    /// <summary>The utility pin's provenance, appended to triage summaries
    /// when a binding was consulted (ticket 030).</summary>
    private static string Suffix(string utilityNote) =>
        utilityNote.Length == 0 ? "" : $"; {utilityNote}";

    /// <summary>Strict-format parse: `VERDICT: single|cross` then optionally
    /// `REPOS: a, b`. Unknown verdict → (null, []) so the caller defaults.</summary>
    internal static (string? Verdict, List<string> Repos) ParseReply(string reply)
    {
        string? verdict = null;
        var repos = new List<string>();
        foreach (var raw in reply.Split('\n'))
        {
            var line = raw.Trim();
            if (verdict is null && line.StartsWith("VERDICT:", StringComparison.OrdinalIgnoreCase))
            {
                verdict = line["VERDICT:".Length..].Trim().ToLowerInvariant();
            }
            else if (line.StartsWith("REPOS:", StringComparison.OrdinalIgnoreCase))
            {
                repos = [.. line["REPOS:".Length..].Split(',')
                    .Select(r => r.Trim())
                    .Where(r => r.Length > 0)];
            }
        }
        return (verdict, repos);
    }
}
