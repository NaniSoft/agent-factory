namespace AgentFactory.App.Fleet;

/// <summary>
/// The routing action for a cross verdict (ticket 019, mechanism settled in
/// ticket 014 / ADR 0004): the org's `.github` repo is bootstrapped lazily at
/// first routing (private — visibility only matters for health files), then
/// the routing issue is created there — `[cross-repo]` title, the original
/// title/body verbatim, backlinks, labelled `agent-factory` so the `.github`
/// intake enqueues its parent run (ticket 020 executes those) — and the
/// original is commented with the autolink and closed as the routing issue's
/// duplicate. Closing is deliberate (ADR 0004): an original left open can be
/// picked up again — guaranteed double work.
/// </summary>
public sealed class CrossRepoRouter(
    ILogger<CrossRepoRouter> logger,
    GitHubService github,
    RunStore store)
{
    public async Task<string> RouteAsync(RunRecord run, string org, IReadOnlyList<string> repos, CancellationToken ct)
    {
        var inbox = $"{org}/.github";

        // 1. Lazy bootstrap (ADR 0004): create the routing inbox if the org
        //    has none. `--private --add-readme`: issues work either way and
        //    an empty repo can't host issues without a first commit.
        if (!await github.RepoExistsAsync(inbox, ct))
        {
            var created = await github.CreateRepoAsync(
                inbox, "Agent Factory cross-repo intake — fleet-routed issues land here", ct);
            if (!created.Ok)
            {
                throw new InvalidOperationException($"lazy .github create on {org} failed: {created.Output}");
            }
            store.Log(run, $"ROUTING: created routing inbox {inbox} (lazy, first routing for {org})");
            logger.LogInformation("org {Org}: created .github routing inbox at first routing", org);
        }

        // 2. The routing issue carries the original verbatim + backlinks and
        //    the opt-in label (the .github intake's gate, ticket 019).
        var label = await github.EnsureOptInLabelExists(inbox, ct);
        if (!label.Ok) store.Log(run, $"label ensure on {inbox} failed (continuing): {label.Output}");

        var routedUrl = await github.CreateIssueAsync(inbox,
            $"[cross-repo] {run.IssueTitle}",
            $"""
            Cross-repo ticket, routed here by Agent Factory triage (ticket 014/019).

            Triage verdict: **cross** — spans {string.Join(", ", repos)}
            Routed from {run.Repo}#{run.IssueNumber}, now closed as this issue's duplicate.

            ---

            # {run.IssueTitle}

            {run.IssueBody}

            ---

            - Original issue: {run.IssueUrl}
            - Org: {org}
            - Named repos (eligible at triage): {string.Join(", ", repos)}
            """,
            "agent-factory", ct);
        var routedNumber = int.Parse(routedUrl.TrimEnd('/').Split('/')[^1]);
        var routedRef = $"{inbox}#{routedNumber}";
        store.Log(run, $"ROUTING: opened {routedRef} carrying the original verbatim");

        // 3. Backlink comment + close-as-duplicate on the original. The
        //    `owner/repo#N` refs autolink cross-repo and generate timeline
        //    backlinks on both sides (ticket 010).
        var comment = await github.CommentOnIssue(run.Repo, run.IssueNumber,
            $"""
            Agent Factory triage classified this issue as **cross-repo** work (spans {string.Join(", ", repos)}).

            Routed to {routedRef} — the parent run will coordinate the per-repo work there. This issue closes as that issue's duplicate.
            """, ct);
        store.Log(run, comment.Ok ? "ROUTING: backlink comment posted" : $"ROUTING: comment failed: {comment.Output}");

        var closed = await github.CloseAsDuplicateAsync(run.Repo, run.IssueNumber, routedUrl, ct);
        if (!closed.Ok)
        {
            throw new InvalidOperationException(
                $"close-as-duplicate of {run.Repo}#{run.IssueNumber} failed: {closed.Output}");
        }
        store.Log(run, $"ROUTING: original closed as duplicate of {routedRef}");

        logger.LogInformation("routed {Repo}#{Number} to {Ref} (repos: {Repos})",
            run.Repo, run.IssueNumber, routedRef, string.Join(", ", repos));
        return routedUrl;
    }
}
