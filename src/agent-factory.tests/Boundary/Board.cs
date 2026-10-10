namespace AgentFactory.Tests.Boundary;

using System.Text;
using System.Text.Json;
using AgentFactory;

/// <summary>
/// The Board, read back over the process's own HTTP surface — the `/api/*` JSON the
/// admin app renders — and the reviewer's hand on it.
/// </summary>
/// <remarks>
/// <para>
/// This used to read the Razor board's HTML. The Razor board is retired (#51): the
/// human surface is now the Prism app the same process serves at the root, and the
/// factory's judgement reaches it as JSON through <c>GET /api/board</c> and
/// <c>GET /api/work-items/{id}</c>. So this reads those endpoints and renders their
/// answer into the same markup the board once produced, which keeps every assertion a
/// test already made about a lane, a card, a round, a diff or an intake row — and
/// keeps it an assertion about the factory's own judgement rather than about the
/// renderer.
/// </para>
/// <para>
/// The reviewer's three decisions are posted to
/// <c>POST /api/work-items/{id}/decisions</c>, which is the one write path that
/// remains: it records the decision and asks the loop to apply it, exactly as the
/// deleted page's handler did.
/// </para>
/// <para>
/// Only the parsing is unchanged from the HTML reader. What changed is where the
/// markup comes from: the factory's serialised judgement rather than a server-rendered
/// page.
/// </para>
/// </remarks>
public sealed class Board
{
    private Board(string html, string? project)
    {
        Html = html;
        Project = project;
    }

    /// <summary>The rendered markup, built from the JSON the app reads.</summary>
    public string Html { get; }

    /// <summary>The project the board is narrowed to, or null for all of them.</summary>
    public string? Project { get; }

    // ---------------------------------------------------------------- reading

    public static async Task<Board> ReadAsync(HttpClient client) => await BuildAsync(client, null);

    /// <summary>
    /// The board narrowed to one project, exactly as a reviewer narrows it: the app
    /// filters the cards it draws by the query string, so this filters the same way and
    /// keeps the same project-in-force statement. Reading it through the same read a
    /// reviewer's browser makes is what makes the test about the board's own behaviour.
    /// </summary>
    public static async Task<Board> ReadForAsync(HttpClient client, string project) =>
        await BuildAsync(client, project);

    /// <summary>
    /// The board as a decision's response rendered it. The JSON answer carries the
    /// factory's refusal, and the renderer keeps it where the reviewer acted — so this
    /// builds the small document a refusal lives in rather than reading the board again.
    /// </summary>
    public static async Task<Board> ReadAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();

        var project = ProjectOf(response);
        var body = await response.Content.ReadAsStringAsync();

        string? refusal = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("refusal", out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                refusal = value.GetString();
            }
        }
        catch (JsonException)
        {
            // A non-JSON body is not a decision answer; there is nothing to refuse.
        }

        var html = new StringBuilder();
        if (refusal is { Length: > 0 })
        {
            html.Append($"<p class=\"refusal\" data-refusal=\"the decision was refused\">{Esc(refusal)}</p>");
        }

        if (project is { Length: > 0 })
        {
            html.Append($"<p data-project-in-force=\"{Esc(project)}\" data-known=\"true\">Showing {Esc(project)}.</p>");
        }

        return new Board(html.ToString(), project);
    }

    /// <summary>
    /// The board and everything on it: <c>GET /api/board</c> for the lanes, cards,
    /// projects, rejections and intake, and <c>GET /api/work-items/{id}</c> once per
    /// card for the rounds, decisions and diffs a card renders. Both are the factory's
    /// own judgement, serialised.
    /// </summary>
    private static async Task<Board> BuildAsync(HttpClient client, string? project)
    {
        var board = await Json(client, "/api/board");

        // Every card, and the ones the filter keeps. The filter is the app's: it narrows
        // the cards and nothing else, so intake and the filter list are computed from the
        // whole board whatever is in force.
        var allCards = new List<Card>();
        foreach (var lane in board.GetProperty("lanes").EnumerateArray())
        {
            var laneName = Str(lane, "lane");
            var laneLabel = Str(lane, "label");
            foreach (var card in lane.GetProperty("cards").EnumerateArray())
            {
                allCards.Add(new Card(laneName, laneLabel, card.Clone()));
            }
        }

        var visible = project is null
            ? allCards
            : allCards.Where(card => Str(card.Json, "project") == project).ToList();

        var details = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var card in visible)
        {
            var id = Str(card.Json, "id");
            if (!details.ContainsKey(id))
            {
                details[id] = await Json(client, $"/api/work-items/{Uri.EscapeDataString(id)}");
            }
        }

        return new Board(Render(board, project, visible, details), project);
    }

    private static async Task<JsonElement> Json(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static string? ProjectOf(HttpResponseMessage response)
    {
        var query = response.RequestMessage?.RequestUri?.Query;
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = pair.IndexOf('=');
            if (at > 0 && pair[..at] == "project")
            {
                return Uri.UnescapeDataString(pair[(at + 1)..]);
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- rendering

    private static string Render(
        JsonElement board,
        string? project,
        IReadOnlyList<Card> cards,
        IReadOnlyDictionary<string, JsonElement> details)
    {
        var projects = board.GetProperty("projects").EnumerateArray().ToList();
        var rejections = board.GetProperty("rejections").EnumerateArray().ToList();
        var autoMerge = board.GetProperty("autoMerge").GetBoolean();

        var served = projects.Select(project => Str(project, "name")).ToList();
        var onTheBoard = served
            .Concat(cards.Select(card => Str(card.Json, "project")))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var html = new StringBuilder();

        // Header: the threshold and the mode, the served projects, the budget.
        var hours = ((int)FactoryConstants.FeedbackThreshold.TotalHours).ToString(System.Globalization.CultureInfo.InvariantCulture);
        html.Append("<p class=\"feedback-threshold\" data-feedback-threshold=\"threshold\"")
            .Append($" data-hours=\"{hours}\"")
            .Append($" data-merges-unattended=\"{(autoMerge ? "on" : "off")}\">");
        if (autoMerge)
        {
            html.Append("A work item left in Review for ").Append(Esc(FactoryConstants.FeedbackThresholdText))
                .Append(" is merged without a review. Ignoring a work item merges it; actively declining one "
                    + "protects the repository. A work item out of rounds is Escalated, never merged.");
        }
        else
        {
            html.Append("Auto-merge is off: a work item left in Review waits for a decision, however long it takes. "
                + "Nothing merges unattended; actively declining one protects the repository. A work item out of "
                + "rounds is Escalated, never merged.");
        }

        html.Append("</p>");

        html.Append("<p class=\"served-projects\">Serving ");
        if (projects.Count == 0)
        {
            html.Append("<span>no projects</span>");
        }
        else
        {
            html.Append($"<span>{projects.Count} project{(projects.Count == 1 ? string.Empty : "s")}</span><ul>");
            foreach (var item in projects)
            {
                html.Append("<li")
                    .Append($" data-served-project=\"{Esc(Str(item, "name"))}\"")
                    .Append($" data-repo-url=\"{Esc(Str(item, "repoUrl"))}\"")
                    .Append($" data-worker-image=\"{Esc(Str(item, "workerImage"))}\"")
                    .Append($" data-llm-model=\"{Esc(Str(item, "llmModel"))}\"")
                    .Append($" data-github-key=\"{Esc(Str(item, "githubKeyName"))}\"")
                    .Append($" data-llm-key=\"{Esc(Str(item, "llmKeyName"))}\"")
                    .Append($" title=\"{Esc(Str(item, "sourceFile"))}\">")
                    .Append(Esc(Str(item, "name")))
                    .Append("</li>");
            }

            html.Append("</ul>");
        }

        html.Append("</p>");

        var budget = board.GetProperty("budget");
        var inUse = Int(budget, "inUse");
        var of = Int(budget, "of");
        html.Append("<p class=\"container-budget\" data-container-budget=\"budget\"")
            .Append($" data-budget=\"{of}\" data-in-flight=\"{inUse}\">")
            .Append($"{inUse} of {of} worker containers in use.")
            .Append("</p>");

        // The project filter: every project always on offer, including the one in force.
        html.Append("<nav class=\"project-filter\" data-project-filter=\"filter\">");
        html.Append("<a href=\"/\" data-project-filtered=\"\"")
            .Append($" data-current=\"{(project is null ? "true" : "false")}\">All projects</a>");
        foreach (var name in onTheBoard)
        {
            html.Append($"<a href=\"/?project={Uri.EscapeDataString(name)}\" data-project-filtered=\"{Esc(name)}\"")
                .Append($" data-current=\"{(project == name ? "true" : "false")}\">{Esc(name)}</a>");
        }

        html.Append("</nav>");

        // Intake, always, and first.
        var intake = board.GetProperty("intake");
        var rows = intake.GetProperty("rows").EnumerateArray().ToList();
        if (rows.Count > 0)
        {
            html.Append("<section class=\"intake\" data-intake=\"intake\"")
                .Append($" data-intake-state=\"{Esc(Str(intake, "status"))}\"")
                .Append($" data-projects=\"{Int(intake, "projects")}\"")
                .Append($" data-polled=\"{Int(intake, "polled")}\"")
                .Append($" data-never-polled=\"{Int(intake, "neverPolled")}\"")
                .Append($" data-failing=\"{Int(intake, "failing")}\">")
                .Append($"<p class=\"intake-summary\" data-intake-summary=\"intake\">{Esc(Str(intake, "summary"))}</p>")
                .Append("<ul>");

            foreach (var row in rows)
            {
                html.Append("<li class=\"intake-project\"")
                    .Append($" data-intake-project=\"{Esc(Str(row, "project"))}\"")
                    .Append($" data-intake-state=\"{Esc(Str(row, "status"))}\"")
                    .Append($" data-repo-url=\"{Esc(Str(row, "repoUrl"))}\"")
                    .Append($" data-open-issues=\"{OpenIssues(row)}\"")
                    .Append($" data-classification=\"{Esc(Str(row, "failure"))}\"")
                    .Append($" data-failures=\"{Int(row, "failures")}\"")
                    .Append($" data-polled-at=\"{Esc(Str(row, "atUtc"))}\"")
                    .Append($" data-again=\"{Esc(Str(row, "again"))}\">")
                    .Append($"<strong>{Esc(Str(row, "project"))}</strong>")
                    .Append($"<span>{Esc(Str(row, "says"))}</span>")
                    .Append("</li>");
            }

            html.Append("</ul></section>");
        }

        // The files the loader refused, reported rather than swallowed.
        if (rejections.Count > 0)
        {
            html.Append("<section class=\"refused-project-files\"><h2>Project files refused</h2><ul>");
            foreach (var rejection in rejections)
            {
                html.Append("<li")
                    .Append($" data-refused-file=\"{Esc(Str(rejection, "fileName"))}\"")
                    .Append($" data-reason=\"{Esc(Str(rejection, "reason"))}\">")
                    .Append(Esc(Str(rejection, "message")))
                    .Append("</li>");
            }

            html.Append("</ul></section>");
        }

        // The filter in force, and whether the factory knows the project.
        if (project is { Length: > 0 } narrowed)
        {
            var known = onTheBoard.Contains(narrowed, StringComparer.Ordinal);
            html.Append("<p class=\"project-in-force\"")
                .Append($" data-project-in-force=\"{Esc(narrowed)}\" data-known=\"{(known ? "true" : "false")}\">")
                .Append($"Showing {Esc(narrowed)}.");
            if (!known)
            {
                html.Append("<span>The factory has never heard of it: no project by that name is being served "
                    + "and none has work waiting.</span>");
            }

            html.Append("</p>");
        }

        // The lanes, grouped by project inside each.
        html.Append("<section class=\"board\">");
        foreach (var lane in board.GetProperty("lanes").EnumerateArray())
        {
            var laneName = Str(lane, "lane");
            html.Append($"<div class=\"swimlane\" data-swimlane=\"{Esc(laneName)}\"><h2>{Esc(Str(lane, "label"))}</h2>");

            foreach (var group in cards
                .Where(card => card.Lane == laneName)
                .GroupBy(card => Str(card.Json, "project"), StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                html.Append($"<section class=\"project-group\" data-project-group=\"{Esc(group.Key)}\"")
                    .Append($" data-project-count=\"{group.Count()}\"><ul>");
                foreach (var card in group)
                {
                    RenderCard(html, card, details, autoMerge);
                }

                html.Append("</ul></section>");
            }

            html.Append("</div>");
        }

        html.Append("</section>");

        return html.ToString();
    }

    private static void RenderCard(
        StringBuilder html,
        Card card,
        IReadOnlyDictionary<string, JsonElement> details,
        bool autoMerge)
    {
        var id = Str(card.Json, "id");
        var detail = details[id];
        var header = detail.GetProperty("workItem");
        var rounds = detail.GetProperty("rounds").EnumerateArray().ToList();
        var decisions = detail.GetProperty("decisions").EnumerateArray().ToList();
        var offered = detail.GetProperty("offeredDecisions").EnumerateArray().Select(d => d.GetString()!).ToList();

        var cause = Str(card.Json, "ending");

        // The countdown is the on-mode's obligation, and it only exists then: with
        // auto-merge off there is nothing a clock could say, so the attribute is not
        // written at all.
        var at = autoMerge && card.Lane == "Review" ? AutoMergeAt(header) : null;
        var autoMergeAttribute = at is { } when ? $" data-auto-merge=\"{when:O}\"" : string.Empty;

        html.Append("<li class=\"work-item\"")
            .Append($" data-work-item=\"{Esc(id)}\"")
            .Append($" data-project=\"{Esc(Str(card.Json, "project"))}\"")
            .Append($" data-issue=\"{Int(card.Json, "issueNumber")}\"")
            .Append($" data-base-branch=\"{Esc(Str(header, "baseBranch"))}\"")
            .Append($" data-round-count=\"{Int(card.Json, "roundCount")}\"")
            .Append($" data-rounds=\"{rounds.Count}\"")
            .Append($" data-decisions=\"{decisions.Count}\"")
            .Append($" data-merge-attempts=\"{Int(header, "mergeAttempts")}\"")
            .Append($" data-cause=\"{Esc(cause)}\"")
            .Append(autoMergeAttribute)
            .Append(">");

        html.Append($"<span class=\"project\">{Esc(Str(card.Json, "project"))}</span>")
            .Append($"<span class=\"issue\">#{Int(card.Json, "issueNumber")} {Esc(Str(card.Json, "title"))}</span>")
            .Append($"<span class=\"base-branch\">base {Esc(Str(header, "baseBranch"))}</span>")
            .Append($"<span class=\"round-count\">round {Int(card.Json, "roundCount")} of {Int(card.Json, "roundCeiling")}</span>");

        if (cause.Length > 0)
        {
            html.Append($"<span class=\"cause\">{Esc(cause)}</span>");
        }

        if (at is { } shipsAt)
        {
            html.Append("<span class=\"auto-merge\">auto-merges at "
                + $"{Esc(shipsAt.ToString("yyyy-MM-dd HH:mm 'UTC'"))} unless a reviewer decides first</span>");
        }

        if (rounds.Count > 0)
        {
            html.Append("<ol class=\"rounds\">");
            foreach (var round in rounds)
            {
                RenderRound(html, id, round);
            }

            html.Append("</ol>");
        }

        if (decisions.Count > 0)
        {
            html.Append("<ol class=\"decided\">");
            foreach (var decision in decisions)
            {
                html.Append("<li class=\"decision-made\"")
                    .Append($" data-decision-made=\"{Esc(Str(decision, "decision"))}\"")
                    .Append($" data-made-on=\"{Esc(id)}\"")
                    .Append($" data-feedback=\"{Esc(Str(decision, "feedback"))}\"")
                    .Append($" data-applied-to=\"{Esc(Str(decision, "appliedTo"))}\"")
                    .Append($" data-decided-utc=\"{Esc(Str(decision, "decidedUtc"))}\">");

                var appliedToLabel = Str(decision, "appliedToLabel");
                if (appliedToLabel.Length > 0)
                {
                    html.Append($"<span class=\"applied-to\">applied to {Esc(appliedToLabel)}</span>");
                }

                var feedback = Str(decision, "feedback");
                if (feedback.Length > 0)
                {
                    html.Append($"<span class=\"reviewer-words\">{Esc(feedback)}</span>");
                }

                html.Append("</li>");
            }

            html.Append("</ol>");
        }

        if (offered.Count > 0)
        {
            html.Append($"<form class=\"decide\" data-decide=\"{Esc(id)}\" method=\"post\">")
                .Append($"<input type=\"hidden\" name=\"workItem\" value=\"{Esc(id)}\" />")
                .Append("<textarea name=\"feedback\"></textarea>");
            foreach (var decision in offered)
            {
                html.Append($"<button type=\"submit\" name=\"decision\" value=\"{Esc(decision)}\"")
                    .Append($" data-decision=\"{Esc(decision)}\">{Esc(decision)}</button>");
            }

            html.Append("</form>");
        }

        html.Append("</li>");
    }

    private static void RenderRound(StringBuilder html, string workItemId, JsonElement round)
    {
        var number = Int(round, "roundNumber");
        var diff = round.TryGetProperty("diff", out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : (JsonElement?)null;
        var state = diff is { } shown ? Str(shown, "state") : "none";

        html.Append("<li class=\"round\"")
            .Append($" data-round=\"{number}\"")
            .Append($" data-outcome=\"{Esc(Str(round, "outcome"))}\"")
            .Append($" data-failure=\"{Esc(Str(round, "failure"))}\"")
            .Append($" data-attempts=\"{Int(round, "attempts")}\"")
            .Append($" data-has-result=\"{(StrOrNull(round, "payload") is { Length: > 0 } ? "true" : "false")}\"")
            .Append($" data-has-log=\"{(StrOrNull(round, "log") is { Length: > 0 } ? "true" : "false")}\"")
            .Append($" data-has-diff=\"{(diff is null ? "false" : "true")}\"")
            .Append($" data-diff-state=\"{Esc(state)}\">");

        html.Append($"<span class=\"round-number\">round {number}</span><span class=\"outcome\">{Esc(Str(round, "outcome"))}</span>");
        var attempts = Int(round, "attempts");
        if (attempts > 1)
        {
            html.Append($"<span class=\"attempts\">{attempts} attempts</span>");
        }

        var failure = Str(round, "failure");
        if (failure.Length > 0)
        {
            html.Append($"<span class=\"failure\">{Esc(failure.ToLowerInvariant())} failure</span>");
        }

        if (diff is { } present)
        {
            if (state is "shown" or "empty" or "unfinished")
            {
                RenderDiff(html, workItemId, number, present);
            }
            else
            {
                html.Append($"<section class=\"diff diff-unavailable\" data-diff=\"round {number}\"")
                    .Append(" data-state=\"unavailable\"")
                    .Append($" data-tree=\"{Esc(Str(present, "tree"))}\">")
                    .Append($"<p class=\"diff-none\">There is no diff for this round: {Esc(Str(present, "unavailableBecause"))}</p>")
                    .Append("</section>");
            }
        }

        if (StrOrNull(round, "payload") is { Length: > 0 } payload)
        {
            html.Append($"<details class=\"result-wrapper\" data-result-wrapper=\"round {number}\">")
                .Append("<summary>what the round's container recorded about itself</summary>")
                .Append($"<pre class=\"result\" data-result=\"round {number}\">{Esc(payload)}</pre></details>");
        }

        if (StrOrNull(round, "agentNote") is { Length: > 0 } note)
        {
            html.Append($"<span class=\"note\" data-note=\"round {number}\">{Esc(note)}</span>");
        }

        if (StrOrNull(round, "log") is { Length: > 0 } log)
        {
            html.Append("<details class=\"round-log\">")
                .Append($"<summary data-log=\"round {number}\">the round's log</summary>")
                .Append($"<pre class=\"log\">{Esc(log)}</pre></details>");
        }

        html.Append("</li>");
    }

    private static void RenderDiff(StringBuilder html, string workItemId, int number, JsonElement diff)
    {
        var files = diff.GetProperty("files").EnumerateArray().ToList();

        html.Append($"<section class=\"diff\" data-diff=\"round {number}\"")
            .Append($" data-state=\"{Esc(Str(diff, "state"))}\"")
            .Append($" data-files=\"{files.Count}\"")
            .Append($" data-total-files=\"{Int(diff, "totalFiles")}\"")
            .Append($" data-omitted-files=\"{Int(diff, "omittedFiles")}\"")
            .Append($" data-omitted-lines=\"{Int(diff, "omittedLines")}\"")
            .Append($" data-tree=\"{Esc(Str(diff, "tree"))}\">");

        if (StrOrNull(diff, "saysAboutAnUnchangedDisk") is { Length: > 0 } unchanged)
        {
            html.Append($"<p class=\"diff-empty\" data-diff-empty=\"round {number}\">{Esc(unchanged)}</p>");
        }
        else
        {
            html.Append($"<p class=\"diff-counts\">{files.Count} of {Int(diff, "totalFiles")} file(s) shown, "
                + $"{Int(diff, "totalFiles")} file(s) in all.</p>");

            if (StrOrNull(diff, "whatIsLeftOff") is { Length: > 0 } leftOff)
            {
                html.Append($"<p class=\"diff-omitted\" data-diff-omitted=\"round {number}\">{Esc(leftOff)}</p>");
            }

            if (StrOrNull(diff, "boundedElsewhere") is { Length: > 0 } bounded)
            {
                html.Append($"<p class=\"diff-bounded\" data-diff-bounded=\"round {number}\">{Esc(bounded)}</p>");
            }

            html.Append("<ol class=\"diff-files\">");
            foreach (var file in files)
            {
                var path = Str(file, "path");
                var open = file.TryGetProperty("open", out var isOpen) && isOpen.GetBoolean();
                html.Append("<li><details class=\"diff-file-details\"")
                    .Append($" data-diff-file=\"{Esc(path)}\"")
                    .Append($" data-open-key=\"{Esc($"{workItemId}/{number}/{path}")}\"")
                    .Append($" data-change=\"{Esc(Str(file, "change"))}\"")
                    .Append($" data-added=\"{Int(file, "added")}\"")
                    .Append($" data-removed=\"{Int(file, "removed")}\"")
                    .Append($" data-binary=\"{(file.TryGetProperty("binary", out var binary) && binary.GetBoolean() ? "true" : "false")}\"")
                    .Append($" data-open=\"{(open ? "true" : "false")}\"")
                    .Append(">")
                    .Append($"<summary>{Esc(path)}</summary>")
                    .Append($"<pre class=\"diff-text\">{Esc(Str(file, "text"))}</pre></details></li>");
            }

            html.Append("</ol>");
        }

        html.Append("</section>");
    }

    private static DateTimeOffset? AutoMergeAt(JsonElement header) =>
        StrOrNull(header, "reviewStartedUtc") is { Length: > 0 } since
            ? DateTimeOffset.Parse(since, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind) + FactoryConstants.FeedbackThreshold
            : null;

    private static string OpenIssues(JsonElement row) =>
        row.TryGetProperty("openIssues", out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : string.Empty;

    private static string? StrOrNull(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static string Esc(string value) => System.Net.WebUtility.HtmlEncode(value);

    private sealed record Card(string Lane, string LaneLabel, JsonElement Json);

    // ---------------------------------------------------------------- the reviewer's hand

    /// <summary>
    /// The reviewer's hand: one decision, posted to the endpoint as the app posts it.
    /// The decision is recorded and the loop asked to apply it; a refusal comes back in
    /// the response's body as the factory's own words.
    /// </summary>
    public static async Task<HttpResponseMessage> DecideAsync(
        HttpClient client,
        Guid workItemId,
        string decision,
        string? feedback = null) =>
        await DecideAtAsync(client, null, workItemId, decision, feedback);

    /// <summary>
    /// A decision posted by hand rather than pressed: whatever the caller puts in the
    /// decision field — including a value no button carries. What is under test is the
    /// value, not a missing field.
    /// </summary>
    public static async Task<HttpResponseMessage> PostByHandAsync(
        HttpClient client,
        Guid workItemId,
        string? decision,
        string? feedback = null) =>
        await DecideAtAsync(client, null, workItemId, decision, feedback);

    /// <summary>
    /// A decision made from a board the reviewer has narrowed to one project. The filter
    /// is a way of looking, not a way of deciding, so it rides the request as a query
    /// string and changes nothing about the decision — and the refusal is rendered back
    /// on the same narrowed board.
    /// </summary>
    public static async Task<HttpResponseMessage> DecideForProjectAsync(
        HttpClient client,
        string project,
        Guid workItemId,
        string decision,
        string? feedback = null) =>
        await DecideAtAsync(client, project, workItemId, decision, feedback);

    /// <summary>
    /// A decision posted by hand from a board narrowed to one project.
    /// </summary>
    public static async Task<HttpResponseMessage> PostByHandForProjectAsync(
        HttpClient client,
        string project,
        Guid workItemId,
        string? decision,
        string? feedback = null) =>
        await DecideAtAsync(client, project, workItemId, decision, feedback);

    private static async Task<HttpResponseMessage> DecideAtAsync(
        HttpClient client,
        string? project,
        Guid workItemId,
        string? decision,
        string? feedback)
    {
        var path = $"/api/work-items/{workItemId:D}/decisions"
            + (project is { Length: > 0 } narrowed ? $"?project={Uri.EscapeDataString(narrowed)}" : string.Empty);

        return await client.PostAsync(
            path,
            System.Net.Http.Json.JsonContent.Create(new { decision, feedback }));
    }

    // ---------------------------------------------------------------- what a board renders

    /// <summary>
    /// The markup of one project's group, up to the next group or the end of the lane.
    /// Scoped to a swimlane where a test says which lane it means, because the same project
    /// has a group in every lane and "one project's cards" is not a thing on its own.
    /// </summary>
    public string ProjectGroup(string project, string? swimlane = null)
    {
        var lane = swimlane is null ? Swimlane(project) : Swimlane(swimlane);
        var marker = $"data-project-group=\"{project}\"";
        var start = lane.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        var end = lane.IndexOf("data-project-group=\"", start + marker.Length, StringComparison.Ordinal);
        return end < 0 ? lane[start..] : lane[start..end];
    }

    /// <summary>Every project the board offers as a filter, in the order it renders them.</summary>
    public IReadOnlyList<string> ProjectFilters() => ValuesOf("data-project-filtered");

    /// <summary>
    /// The project the board is currently narrowed to, or empty for all of them. Read off
    /// the board's own statement of it rather than out of the URL, because what matters is
    /// what a reviewer can see: a filter that is applied and not said is a filter nobody
    /// can tell is in force.
    /// </summary>
    /// <remarks>
    /// Read as a browser would rather than as the board wrote it. A project name is a
    /// reviewer's own project's name, so it can carry an ampersand or a quote, and asserting
    /// on the raw markup would be asserting on the escaping instead of on the filter.
    /// </remarks>
    public string ProjectInForce() => System.Net.WebUtility.HtmlDecode(
        AttributeOf(ElementCarrying("data-project-in-force"), "data-project-in-force") ?? string.Empty);

    /// <summary>
    /// Whether the project the board is narrowed to is one the factory serves or has work
    /// for. False means the board is empty because the name is unknown, which is a different
    /// thing from a project that simply has nothing waiting. Null when the board is not
    /// filtered at all.
    /// </summary>
    public bool? ProjectIsKnown()
    {
        var value = AttributeOf(ElementCarrying("data-project-in-force"), "data-known");
        return value is null ? null : value == "true";
    }

    /// <summary>
    /// The opening tag of the first element carrying a marker, whatever its value. The
    /// existing helpers all need to be told the value first, which is the wrong shape for a
    /// marker whose whole point is that the value is the thing under test.
    /// </summary>
    private string ElementCarrying(string marker)
    {
        var name = $"{marker}=\"";
        var at = Html.IndexOf(name, StringComparison.Ordinal);
        return at < 0 ? string.Empty : Html[at..Html.IndexOf('>', at)];
    }

    /// <summary>Reads an attribute off the first element carrying the given marker.</summary>
    public string? Attribute(string marker, string markerValue, string attribute)
    {
        var start = Html.IndexOf($"{marker}=\"{markerValue}\"", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        return AttributeOf(Html[start..Html.IndexOf('>', start)], attribute);
    }

    /// <summary>Reads an attribute off one element's own markup.</summary>
    public static string? AttributeOf(string element, string attribute)
    {
        var name = $"{attribute}=\"";
        var at = element.IndexOf(name, StringComparison.Ordinal);
        return at < 0 ? null : element[(at + name.Length)..element.IndexOf('"', at + name.Length)];
    }

    /// <summary>Reads the attribute off the first element carrying the marker, and fails if it is not there.</summary>
    public string Rendered(string marker, string markerValue, string attribute) =>
        Attribute(marker, markerValue, attribute)
        ?? throw new Xunit.Sdk.XunitException(
            $"the board rendered nothing marked {marker}=\"{markerValue}\", so {attribute} cannot be read");

    /// <summary>The markup of one swimlane, up to the next swimlane.</summary>
    public string Swimlane(string swimlane)
    {
        // A lane is named by its slug in the markup and read by a reviewer by its label;
        // only "In Progress" differs between the two, so normalise a label to its slug.
        var slug = swimlane == "In Progress" ? "InProgress" : swimlane;
        var marker = $"data-swimlane=\"{slug}\"";
        var start = Html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        var next = Html.IndexOf("data-swimlane=\"", start + marker.Length, StringComparison.Ordinal);
        return next < 0 ? Html[start..] : Html[start..next];
    }

    public bool Renders(string text) => Html.Contains(text, StringComparison.Ordinal);

    /// <summary>
    /// One swimlane's markup as a reviewer reads it, with the HTML entities decoded.
    /// A result payload is a diff, so it is full of characters the board escapes on the
    /// way out; asserting on the raw markup would be asserting on the escaping.
    /// </summary>
    public string Read(string swimlane) => System.Net.WebUtility.HtmlDecode(Swimlane(swimlane));

    /// <summary>Every value of a marker attribute, in document order.</summary>
    public IReadOnlyList<string> ValuesOf(string marker) => ValuesOf(Html, marker);

    /// <summary>Every value of a marker attribute inside a piece of markup, in document order.</summary>
    public static IReadOnlyList<string> ValuesOf(string markup, string marker)
    {
        var values = new List<string>();
        var name = $"{marker}=\"";
        for (var at = markup.IndexOf(name, StringComparison.Ordinal); at >= 0; at = markup.IndexOf(name, at + name.Length, StringComparison.Ordinal))
        {
            values.Add(markup[(at + name.Length)..markup.IndexOf('"', at + name.Length)]);
        }

        return values;
    }

    /// <summary>
    /// One round's diff as the board rendered it, decoded. The round is named the way the
    /// board names it, so a test reads the reviewer's surface rather than reaching into
    /// the store for what it ought to have rendered.
    /// </summary>
    public RenderedDiff DiffOn(int roundNumber)
    {
        var marker = $"data-diff=\"round {roundNumber}\"";
        var start = Html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"the board rendered no diff for round {roundNumber}, so there is nothing to read");
        }

        var end = Html.IndexOf("</section>", start, StringComparison.Ordinal);
        var section = end < 0 ? Html[start..] : Html[start..end];
        var open = section[..section.IndexOf('>')];

        return new RenderedDiff(
            State: AttributeOf(open, "data-state") ?? string.Empty,
            FilesShown: int.TryParse(AttributeOf(open, "data-files"), out var shown) ? shown : 0,
            TotalFiles: int.TryParse(AttributeOf(open, "data-total-files"), out var total) ? total : 0,
            OmittedFiles: int.TryParse(AttributeOf(open, "data-omitted-files"), out var omittedFiles) ? omittedFiles : 0,
            OmittedLines: int.TryParse(AttributeOf(open, "data-omitted-lines"), out var omittedLines) ? omittedLines : 0,
            Tree: System.Net.WebUtility.HtmlDecode(AttributeOf(open, "data-tree") ?? string.Empty),
            Files: FilesInDiff(section),
            Text: System.Net.WebUtility.HtmlDecode(section));
    }

    /// <summary>Whether the board rendered a diff section for a round at all.</summary>
    public bool RenderedDiffOn(int roundNumber) =>
        Html.Contains($"data-diff=\"round {roundNumber}\"", StringComparison.Ordinal);

    /// <summary>
    /// One file's section of a diff: the path as the board named it, what it says happened,
    /// the counts, and git's own text for it — all decoded, because a diff is full of
    /// characters the board escapes on the way out and asserting on the raw markup would
    /// be asserting on the escaping.
    /// </summary>
    private static IReadOnlyList<RenderedDiffFile> FilesInDiff(string section)
    {
        var files = new List<RenderedDiffFile>();

        foreach (var at in IndicesOf(section, "data-diff-file=\""))
        {
            var open = section[at..section.IndexOf('>', at)];

            var pre = section.IndexOf("<pre", at, StringComparison.Ordinal);
            var close = pre < 0 ? -1 : section.IndexOf("</pre>", pre, StringComparison.Ordinal);
            var body = pre >= 0 && close > pre
                ? System.Net.WebUtility.HtmlDecode(section[(section.IndexOf('>', pre) + 1)..close])
                : string.Empty;

            files.Add(new RenderedDiffFile(
                Path: System.Net.WebUtility.HtmlDecode(AttributeOf(open, "data-diff-file") ?? string.Empty),
                Change: AttributeOf(open, "data-change") ?? string.Empty,
                Added: int.TryParse(AttributeOf(open, "data-added"), out var added) ? added : 0,
                Removed: int.TryParse(AttributeOf(open, "data-removed"), out var removed) ? removed : 0,
                Binary: AttributeOf(open, "data-binary") == "true",
                Open: AttributeOf(open, "data-open") == "true",
                Text: body));
        }

        return files;
    }

    private static IEnumerable<int> IndicesOf(string markup, string needle)
    {
        for (var at = markup.IndexOf(needle, StringComparison.Ordinal);
             at >= 0;
             at = markup.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            yield return at;
        }
    }

    /// <summary>
    /// What one round's log says, decoded, or null when the board rendered no log for it.
    /// </summary>
    public string? LogOn(int roundNumber) => Following($"data-log=\"round {roundNumber}\"");

    /// <summary>
    /// What one round's result payload says, decoded, or null when it rendered none. Read
    /// backwards from the marker, which the board puts on the payload's own element — a
    /// round's log is the next <c>&lt;pre&gt;</c> after it, and reading forwards would
    /// return the log.
    /// </summary>
    public string? ResultOn(int roundNumber) => Preceding($"data-result=\"round {roundNumber}\"");

    private string? Preceding(string marker)
    {
        var at = Html.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        return Between(
            Html.LastIndexOf("<pre", at, StringComparison.Ordinal),
            Html.IndexOf("</pre>", at, StringComparison.Ordinal));
    }

    private string? Following(string marker)
    {
        var at = Html.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var open = Html.IndexOf("<pre", at, StringComparison.Ordinal);
        return Between(open, open < 0 ? -1 : Html.IndexOf("</pre>", open, StringComparison.Ordinal));
    }

    private string? Between(int open, int close) =>
        open < 0 || close < open
            ? null
            : System.Net.WebUtility.HtmlDecode(Html[(Html.IndexOf('>', open) + 1)..close]);

    /// <summary>The markup of the decision form the board rendered for one work item.</summary>
    public string DecisionFormFor(Guid workItemId) => FormAt($"data-decide=\"{workItemId:D}\"");

    /// <summary>The markup of the first decision form on the page, whichever work item it is for.</summary>
    public string FirstDecisionForm() => FormAt("data-decide=\"");

    /// <summary>
    /// The decisions the board rendered for one work item, oldest first, as a reviewer
    /// reads them: which of the three it was, the reviewer's own words with it, and the
    /// swimlane the loop put the work item in to apply it — empty until it has.
    /// </summary>
    public IReadOnlyList<(string Decision, string Feedback, string AppliedTo)> DecisionsOn(Guid workItemId)
    {
        const string marker = "data-decision-made";
        var decisions = new List<(string Decision, string Feedback, string AppliedTo)>();

        for (var at = Html.IndexOf($"{marker}=\"", StringComparison.Ordinal);
             at >= 0;
             at = Html.IndexOf($"{marker}=\"", at + marker.Length, StringComparison.Ordinal))
        {
            var element = Html[at..Html.IndexOf('>', at)];
            if (AttributeOf(element, "data-made-on") != workItemId.ToString("D"))
            {
                continue;
            }

            decisions.Add((
                ValuesOf(element, marker)[0],
                Decoded(AttributeOf(element, "data-feedback")),
                Decoded(AttributeOf(element, "data-applied-to"))));
        }

        return decisions;
    }

    /// <summary>The refusal the board rendered, if it rendered one, in the reviewer's words.</summary>
    public string? Refusal()
    {
        const string marker = "data-refusal=\"";
        var at = Html.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var from = Html.IndexOf('>', at) + 1;
        var to = Html.IndexOf("</p>", from, StringComparison.Ordinal);
        return to < 0 ? null : System.Net.WebUtility.HtmlDecode(Html[from..to].Trim());
    }

    /// <summary>The form around a marker, from its opening tag to its closing tag.</summary>
    private string FormAt(string marker)
    {
        var at = Html.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            return string.Empty;
        }

        var open = Html.LastIndexOf("<form", at, StringComparison.Ordinal);
        var close = Html.IndexOf("</form>", at, StringComparison.Ordinal);
        return open < 0 || close < 0 ? string.Empty : Html[open..(close + "</form>".Length)];
    }

    /// <summary>An attribute value as a browser reads it, rather than as the board wrote it.</summary>
    private static string Decoded(string? value) =>
        value is null ? string.Empty : System.Net.WebUtility.HtmlDecode(value);
}

/// <summary>One round's diff as the board rendered it, read back over HTTP and decoded.</summary>
public sealed record RenderedDiff(
    string State,
    int FilesShown,
    int TotalFiles,
    int OmittedFiles,
    int OmittedLines,
    string Tree,
    IReadOnlyList<RenderedDiffFile> Files,
    string Text);

/// <summary>One file's section of a rendered diff: the board's index line and git's own text.</summary>
public sealed record RenderedDiffFile(
    string Path,
    string Change,
    int Added,
    int Removed,
    bool Binary,
    bool Open,
    string Text);
