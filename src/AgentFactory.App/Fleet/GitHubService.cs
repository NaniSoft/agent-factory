using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentFactory.App.Fleet;

/// <summary>
/// GitHub side of any enrolled repo via the authenticated `gh` CLI: issue
/// polling, issue comments, PR creation, and label provisioning — the repo
/// arrives per call (ticket 017; the fleet is multi-repo). The host owns this
/// flow exclusively (ADR 0001) — fleet agents never see `gh`. Multiline bodies
/// go over stdin via --body-file - so no quoting can corrupt them.
/// </summary>
public sealed class GitHubService(FleetOptions options)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public async Task<ProcResult> EnsureOptInLabelExists(string repo, CancellationToken ct = default)
    {
        var result = await Proc.RunAsync("gh",
            $"label create {options.OptInLabel} -R {repo} " +
            "--description \"Opt this issue into the Agent Factory agent fleet\" --color 1D76DB", ct: ct);
        // Non-zero with "already exists" is the idempotent success case.
        return result.Ok || result.Output.Contains("already exists") ? result with { ExitCode = 0 } : result;
    }

    /// <summary>Issues currently carrying the opt-in label (open only), via a
    /// conditional GET: pass the etag the last call returned and a 304 comes
    /// back rate-limit-free when the labelled set is unchanged (ticket 010).
    /// Label adds and removals both change the set, so the etag catches them.</summary>
    public async Task<(ConditionalGet Get, List<IssueSnapshot> Issues)> ListLabelledIssuesConditionalAsync(
        string repo, string? etag, CancellationToken ct = default)
    {
        var get = await GetConditionalAsync(
            $"repos/{repo}/issues?state=open&labels={options.OptInLabel}&per_page=100", etag, ct);
        return (get, get.NotModified ? [] : ParseRestIssues(get.Body));
    }

    /// <summary>Open issues oldest-first — the `all`-intake backbone
    /// (ADR 0003). The enrollment backlog drains oldest-first under its cap;
    /// new issues land at the ascending list's tail. Both change the
    /// representation, so the etag catches them.</summary>
    public async Task<(ConditionalGet Get, List<IssueSnapshot> Issues)> ListOpenIssuesOldestFirstConditionalAsync(
        string repo, string? etag, CancellationToken ct = default)
    {
        var get = await GetConditionalAsync(
            $"repos/{repo}/issues?state=open&sort=created&direction=asc&per_page=100", etag, ct);
        return (get, get.NotModified ? [] : ParseRestIssues(get.Body));
    }

    /// <summary>One enrolled org's repos (ticket 010's verified shape).
    /// Ordering is not contractual — callers diff by <c>nameWithOwner</c>,
    /// never by position.</summary>
    public async Task<List<OrgRepoSnapshot>> ListOrgReposAsync(string org, CancellationToken ct = default)
    {
        var result = await Proc.RunAsync("gh",
            $"repo list {org} --limit 3000 --json nameWithOwner,isArchived,isFork,hasIssuesEnabled", ct: ct);
        if (!result.Ok) throw new InvalidOperationException($"gh repo list on {org} failed: {result.Output}");
        var payload = JsonSerializer.Deserialize<List<GhRepo>>(result.Output, JsonOpts) ?? [];
        return payload.Select(r => new OrgRepoSnapshot(r.NameWithOwner, r.IsArchived, r.IsFork, r.HasIssuesEnabled)).ToList();
    }

    /// <summary>Whether an org exists and is listable by the authenticated
    /// user (ticket 022's enroll probe). A one-repo list is the cheapest
    /// existence check that still proves read access.</summary>
    public async Task<bool> OrgExistsAsync(string org, CancellationToken ct = default)
    {
        var result = await Proc.RunAsync("gh",
            $"repo list {org} --limit 1 --json nameWithOwner", ct: ct);
        return result.Ok;
    }

    /// <summary>Outcome of a conditional GET: <see cref="NotModified"/> means
    /// the representation is unchanged since the passed etag was captured —
    /// nothing new, and 304s don't count against the rate limit.</summary>
    public sealed record ConditionalGet(bool NotModified, string? Etag, string Body);

    /// <summary>`gh api -i` with an optional If-None-Match: prints the status
    /// line and headers, a blank line, then the body. A 304 keeps the headers
    /// on stdout (etag included) but makes gh exit non-zero with its own
    /// "gh: HTTP 304" on stderr — that's the not-modified signal, not a
    /// failure (probed live, ticket 018; ticket 010's exit-0 shape was
    /// gh-version optimistic).</summary>
    private async Task<ConditionalGet> GetConditionalAsync(string pathAndQuery, string? etag, CancellationToken ct)
    {
        var headers = etag is null ? "" : " -H " + QuoteArg($"If-None-Match: {etag}");
        var result = await Proc.RunAsync("gh", $"api -i {QuoteArg(pathAndQuery)}{headers}", ct: ct);

        var lines = result.Output.Split('\n');
        var headEnd = Array.FindIndex(lines, l => l.Trim().Length == 0);
        var head = headEnd < 0 ? lines : lines[..headEnd];
        var status = head.Length > 0 ? head[0] : "";
        var notModified = status.Contains("304");
        if (!result.Ok && !notModified)
        {
            throw new InvalidOperationException($"gh api on {pathAndQuery} failed: {result.Output}");
        }

        var etagHeader = head.FirstOrDefault(h => h.StartsWith("etag:", StringComparison.OrdinalIgnoreCase));
        var body = notModified ? "" : (headEnd < 0 ? "" : string.Join('\n', lines[(headEnd + 1)..]).Trim());
        return new ConditionalGet(notModified, etagHeader?["etag:".Length..].Trim(), body);
    }

    /// <summary>Parse a REST issues payload. The REST endpoint also returns
    /// pull requests — entries carrying a <c>pull_request</c> object are not
    /// issues and are dropped.</summary>
    private static List<IssueSnapshot> ParseRestIssues(string body)
    {
        var payload = JsonSerializer.Deserialize<List<RestIssue>>(body, JsonOpts) ?? [];
        return payload.Where(i => i.PullRequest is null)
            .Select(i => new IssueSnapshot(i.Number, i.Title ?? "", i.Body ?? "", i.Url, ParseDate(i.UpdatedAt)))
            .ToList();
    }

    public Task<ProcResult> CommentOnIssue(string repo, int number, string body, CancellationToken ct = default) =>
        Proc.RunStdinAsync("gh",
            $"issue comment {number} -R {repo} --body-file -", body, ct: ct);

    /// <summary>Comment and capture its id (trailing …#issuecomment-&lt;id&gt;
    /// in gh's output) — the parent run's tracking comment is edited in place
    /// as children land (ticket 020).</summary>
    public async Task<long> CommentOnIssueTrackingAsync(
        string repo, int number, string body, CancellationToken ct = default)
    {
        var result = await CommentOnIssue(repo, number, body, ct);
        if (!result.Ok) throw new InvalidOperationException($"comment on {repo}#{number} failed: {result.Output}");
        var tail = result.Output.Trim().Split('#')[^1];
        return long.TryParse(tail.Replace("issuecomment-", ""), out var id)
            ? id
            : throw new InvalidOperationException($"could not parse comment id from: {result.Output}");
    }

    /// <summary>Edit a comment in place (gh api PATCH), by id.</summary>
    public Task<ProcResult> UpdateCommentAsync(
        string repo, long commentId, string body, CancellationToken ct = default) =>
        Proc.RunStdinAsync("gh",
            $"api {QuoteArg($"repos/{repo}/issues/comments/{commentId}")} -X PATCH --input -",
            JsonSerializer.Serialize(new { body }), ct: ct);

    /// <summary>Label names currently on an issue — how the triage override
    /// labels (`agent-factory:here` / `:cross`, ticket 019) are read.</summary>
    public async Task<List<string>> GetIssueLabelsAsync(string repo, int number, CancellationToken ct = default)
    {
        var result = await Proc.RunAsync("gh",
            $"api {QuoteArg($"repos/{repo}/issues/{number}")} --jq .labels",
            ct: ct);
        if (!result.Ok) throw new InvalidOperationException($"label read on {repo}#{number} failed: {result.Output}");
        var payload = JsonSerializer.Deserialize<List<GhLabel>>(result.Output, JsonOpts) ?? [];
        return [.. payload.Select(l => l.Name)];
    }

    /// <summary>Whether a repo exists (404 is a no, not a throw) — the lazy
    /// `.github` bootstrap check (ticket 019).</summary>
    public async Task<bool> RepoExistsAsync(string repo, CancellationToken ct = default)
    {
        var result = await Proc.RunAsync("gh", $"api {QuoteArg($"repos/{repo}")} -q .full_name", ct: ct);
        return result.Ok && result.Output.Contains(repo);
    }

    /// <summary>Create a repo (the `.github` routing inbox, private +
    /// readme-seeded per ADR 0004; visibility only matters for health
    /// files, not issues).</summary>
    public Task<ProcResult> CreateRepoAsync(string repo, string description, CancellationToken ct = default) =>
        Proc.RunAsync("gh",
            $"repo create {repo} --private --add-readme --description {QuoteArg(description)}", ct: ct);

    /// <summary>Open an issue with a label; returns the new issue's URL
    /// (first stdout line).</summary>
    public async Task<string> CreateIssueAsync(
        string repo, string title, string body, string label, CancellationToken ct = default)
    {
        var result = await Proc.RunStdinAsync("gh",
            $"issue create -R {repo} --title {QuoteArg(title)} --body-file - --label {label}", body, ct: ct);
        if (!result.Ok) throw new InvalidOperationException($"issue create on {repo} failed: {result.Output}");
        return result.Output.Split('\n')[0].Trim();
    }

    /// <summary>Close an issue as a duplicate of another (cross-repo
    /// `owner/repo#N` refs allowed — ticket 010's verified shape).</summary>
    public Task<ProcResult> CloseAsDuplicateAsync(
        string repo, int number, string duplicateOf, CancellationToken ct = default) =>
        Proc.RunAsync("gh",
            $"issue close {number} -R {repo} --reason duplicate --duplicate-of {QuoteArg(duplicateOf)}", ct: ct);

    public Task<ProcResult> CreatePullRequest(string repo, string branch, string title, string body, CancellationToken ct = default) =>
        Proc.RunStdinAsync("gh",
            $"pr create -R {repo} --base {options.BaseBranch} --head {branch} " +
            $"--title {QuoteArg(title)} --body-file -", body, ct: ct);

    /// <summary>Single-line arg quoting (MSVCRT rules: escape backslashes before quotes, then quotes).</summary>
    private static string QuoteArg(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            if (c is '"' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    private static DateTimeOffset ParseDate(string jsonDate) =>
        DateTimeOffset.TryParse(jsonDate, out var d) ? d : DateTimeOffset.UtcNow;

    /// <summary>The REST shape of <c>repos/{o}/{r}/issues</c> — field names
    /// differ from gh's issue-list shape, and <c>pull_request</c> is present
    /// only on PRs (the issues endpoint returns both).</summary>
    private sealed record RestIssue(
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("html_url")] string Url,
        [property: JsonPropertyName("updated_at")] string UpdatedAt,
        [property: JsonPropertyName("pull_request")] JsonElement? PullRequest);

    private sealed record GhRepo(
        [property: JsonPropertyName("nameWithOwner")] string NameWithOwner,
        [property: JsonPropertyName("isArchived")] bool IsArchived,
        [property: JsonPropertyName("isFork")] bool IsFork,
        [property: JsonPropertyName("hasIssuesEnabled")] bool HasIssuesEnabled);

    private sealed record GhLabel([property: JsonPropertyName("name")] string Name);
}

/// <summary>One repo of an enrolled org, as the scan saw it.</summary>
public sealed record OrgRepoSnapshot(string NameWithOwner, bool IsArchived, bool IsFork, bool HasIssuesEnabled);
