namespace AgentFactory.Tests.Boundary;

using System.Text.RegularExpressions;

/// <summary>
/// What the board renders, read back over its real HTTP surface, and the reviewer's
/// hand on it. The factory's only surface a reviewer has and its only write path, so it
/// is both what a test asserts against and what a test drives.
/// </summary>
public sealed class Board
{
    private Board(string html) => Html = html;

    public string Html { get; }

    public static async Task<Board> ReadAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/");
        response.EnsureSuccessStatusCode();
        return new Board(await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The board narrowed to one project, exactly as a reviewer narrows it: a GET with the
    /// project in the query string. Reading it this way rather than filtering the markup
    /// afterwards is what makes the test about the board's own behaviour — a filter that
    /// only existed in the test's hands would pass here and fail in front of a reviewer.
    /// </summary>
    public static async Task<Board> ReadForAsync(HttpClient client, string project)
    {
        using var response = await client.GetAsync($"/?project={Uri.EscapeDataString(project)}");
        response.EnsureSuccessStatusCode();
        return new Board(await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The board as a response rendered it, which for a decision is where a refusal
    /// lives: a refusal is on the page the reviewer is looking at now, and reading the
    /// board again afterwards would find a clean one and lose it.
    /// </summary>
    public static async Task<Board> ReadAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return new Board(await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The markup of one project's group, up to the next group or the end of the board.
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

    /// <summary>
    /// A decision pressed on a board the reviewer has narrowed to one project. It reads
    /// that page and posts to that page's own form, because the filter rides on the form's
    /// action: pressing a button found on the unfiltered board would be a decision made
    /// from a different page than the one the reviewer is looking at.
    /// </summary>
    public static async Task<HttpResponseMessage> DecideForProjectAsync(
        HttpClient client,
        string project,
        Guid workItemId,
        string decision,
        string? feedback = null)
    {
        var board = await ReadForAsync(client, project);
        var form = board.DecisionFormFor(workItemId);
        if (form.Length == 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"the board filtered to {project} rendered no decision form for that work item");
        }

        var fields = FieldsFor(form, workItemId)
            .Concat(Fields(form, "button").Where(button => button.Value == decision))
            .ToList();

        return await SubmitAsync(client, form, fields, feedback);
    }

    /// <summary>
    /// A decision posted by hand from a board filtered to one project. What the filter
    /// must not change is what the board refuses, so this goes through the filtered form
    /// with a value the board did not offer.
    /// </summary>
    public static async Task<HttpResponseMessage> PostByHandForProjectAsync(
        HttpClient client,
        string project,
        Guid workItemId,
        string? decision,
        string? feedback = null)
    {
        var board = await ReadForAsync(client, project);
        var form = board.DecisionFormFor(workItemId) is { Length: > 0 } own
            ? own
            // The page's own form, borrowed for its antiforgery token — which is what a
            // reviewer holding a stale page has. Without this the post would be refused for
            // having no token rather than for what it is asking, and the test would be
            // asserting the wrong refusal.
            : board.FirstDecisionForm();

        if (form.Length == 0)
        {
            throw new Xunit.Sdk.XunitException(
                $"the board filtered to {project} rendered no decision form at all, so there is "
                    + "nothing on it to post from");
        }

        var fields = FieldsFor(form, workItemId);
        fields.Add(new KeyValuePair<string, string>("decision", decision ?? string.Empty));

        return await SubmitAsync(client, form, fields, feedback);
    }

    /// <summary>
    /// The reviewer's hand: one decision, submitted to the board as the form post it is.
    /// The button pressed is the button the board rendered, the field names are the
    /// board's, and the antiforgery token is the one the board issued — a post without
    /// one is not a decision at all, and the board refuses it. A work item the board
    /// rendered no form for — because it is not in Review — borrows the page's form for
    /// its token, which is what a reviewer holding a stale page has, so that whatever
    /// refuses such a post is the refusal itself rather than a missing token.
    /// </summary>
    public static async Task<HttpResponseMessage> DecideAsync(
        HttpClient client,
        Guid workItemId,
        string decision,
        string? feedback = null)
    {
        var board = await ReadAsync(client);
        var form = board.DecisionFormFor(workItemId);
        if (form.Length == 0)
        {
            form = board.FirstDecisionForm();
        }

        if (form.Length == 0)
        {
            throw new Xunit.Sdk.XunitException(
                "the board rendered no decision form, so there is nothing on it to decide with");
        }

        var fields = FieldsFor(form, workItemId)
            .Concat(Fields(form, "button").Where(button => button.Value == decision))
            .ToList();

        return await SubmitAsync(client, form, fields, feedback);
    }

    /// <summary>
    /// A decision posted by hand rather than pressed: the board's own fields, the board's
    /// own token, and whatever the caller puts in the decision field — including a value
    /// no button carries. What is under test is the value, not a missing field.
    /// </summary>
    public static async Task<HttpResponseMessage> PostByHandAsync(
        HttpClient client,
        Guid workItemId,
        string? decision,
        string? feedback = null)
    {
        var board = await ReadAsync(client);
        var form = board.DecisionFormFor(workItemId) is { Length: > 0 } own
            ? own
            : board.FirstDecisionForm();

        if (form.Length == 0)
        {
            throw new Xunit.Sdk.XunitException(
                "the board rendered no decision form, so there is nothing on it to decide with");
        }

        var fields = FieldsFor(form, workItemId);
        fields.Add(new KeyValuePair<string, string>("decision", decision ?? string.Empty));

        return await SubmitAsync(client, form, fields, feedback);
    }

    private static async Task<HttpResponseMessage> SubmitAsync(
        HttpClient client,
        string form,
        List<KeyValuePair<string, string>> fields,
        string? feedback)
    {
        if (feedback is not null)
        {
            fields.Add(new KeyValuePair<string, string>("feedback", feedback));
        }

        using var content = new FormUrlEncodedContent(fields);
        return await client.PostAsync(ActionOf(form), content);
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
        var marker = $"data-swimlane=\"{swimlane}\"";
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

        // Scoped to the diff's own `<section>`, which is where it ends. The page also
        // carries a `data-diff-file` selector in its script — the fold restore reads it —
        // and a section that ran to the end of the document would pick that up as a file
        // the board rendered.
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

            // The <pre> of this file's own text, up to the next file or the end of the
            // section. Read from the board rather than reconstructed, so a test says what
            // the reviewer sees.
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
    /// <remarks>
    /// Read forwards from the log's own marker, which the board puts on the summary that
    /// opens the log's details element. The log's text is the first <c>&lt;pre&gt;</c>
    /// after that; a round with a payload has an earlier one on the card and one without
    /// has only the log's, so this direction reads the log in both shapes.
    /// </remarks>
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

    /// <summary>
    /// Where a form posts to, as a browser would read it. The board escapes an ampersand in
    /// the action's query string into an entity, which a browser decodes before following
    /// it — so a test that posted to the raw attribute would be posting to a URL carrying a
    /// literal `&amp;project=nexus` and the page would see no project at all. That is the
    /// escaping being read as the wire format, and it is the sort of thing that makes a
    /// test pass against markup a reviewer would never get.
    /// </summary>
    private static string ActionOf(string form) => System.Net.WebUtility.HtmlDecode(
        AttributeOf(form[..(form.IndexOf('>') + 1)], "action") ?? "/");

    /// <summary>An attribute value as a browser reads it, rather than as the board wrote it.</summary>
    private static string Decoded(string? value) =>
        value is null ? string.Empty : System.Net.WebUtility.HtmlDecode(value);

    /// <summary>
    /// The form's own fields, with the work item it is about set to this one. The field
    /// names are the board's, and the work item is the one field the form carries that
    /// holds a work item's id — the antiforgery token never does, which is how it is
    /// found rather than by hard-coding a name. A form the board rendered for a
    /// different work item, because the reviewer is holding a page it has since decided
    /// on, is told which work item this post is about.
    /// </summary>
    private static List<KeyValuePair<string, string>> FieldsFor(string form, Guid workItemId)
    {
        var fields = Fields(form, "input").ToList();
        var about = fields.FindIndex(field => Guid.TryParse(field.Value, out _));

        if (about < 0)
        {
            throw new Xunit.Sdk.XunitException(
                "the board's decision form carries no work item, so a post made from it is not about anything");
        }

        fields[about] = new KeyValuePair<string, string>(fields[about].Key, workItemId.ToString("D"));
        return fields;
    }

    /// <summary>The named fields a form carries, as a browser would send them.</summary>
    private static IReadOnlyList<KeyValuePair<string, string>> Fields(string form, string tagName) =>        Tag.Matches(form)
            .Select(match => match.Value)
            .Where(tag => tag.StartsWith($"<{tagName}", StringComparison.Ordinal))
            .Select(tag => (Name: AttributeOf(tag, "name"), Value: AttributeOf(tag, "value")))
            .Where(field => field.Name is not null)
            .Select(field => new KeyValuePair<string, string>(field.Name!, field.Value ?? string.Empty))
            .ToList();

    private static readonly Regex Tag = new(
        "<(?<name>input|button|textarea|select)[^>]*>",
        RegexOptions.Compiled);
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
