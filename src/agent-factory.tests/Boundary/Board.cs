namespace AgentFactory.Tests.Boundary;

/// <summary>
/// What the board renders, read back over its real HTTP surface. The factory's only
/// surface a reviewer has, so it is what a test asserts against.
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

    /// <summary>Reads an attribute off the first element carrying the given marker.</summary>
    public string? Attribute(string marker, string markerValue, string attribute)
    {
        var start = Html.IndexOf($"{marker}=\"{markerValue}\"", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var element = Html[start..Html.IndexOf('>', start)];
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
    public IReadOnlyList<string> ValuesOf(string marker)
    {
        var values = new List<string>();
        var name = $"{marker}=\"";
        for (var at = Html.IndexOf(name, StringComparison.Ordinal); at >= 0; at = Html.IndexOf(name, at + name.Length, StringComparison.Ordinal))
        {
            values.Add(Html[(at + name.Length)..Html.IndexOf('"', at + name.Length)]);
        }

        return values;
    }
}
