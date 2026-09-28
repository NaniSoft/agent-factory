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

    private static string ActionOf(string form) =>
        AttributeOf(form[..(form.IndexOf('>') + 1)], "action") ?? "/";

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
