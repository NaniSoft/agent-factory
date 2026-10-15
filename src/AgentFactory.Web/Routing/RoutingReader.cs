namespace AgentFactory.Routing;

using System.Text.Json;
using AgentFactory.WorkItems;

/// <summary>
/// What became of a routing round's verdict: either the proposal it wrote, or the reason it
/// could not be read. One of the two is always there, which is what makes "the round said
/// this is a build ticket" and "the round said nothing" two different events rather than one.
/// </summary>
/// <param name="Proposal">The verdict, or null when it could not be read.</param>
/// <param name="UnreadableBecause">
/// The round's own words for why there is no verdict, or null when there is one. Always the
/// exact opposite of <paramref name="Proposal"/>, so no caller has to decide which half it
/// is looking at.
/// </param>
public sealed record RoutingOutcome(RoutingProposal? Proposal, string? UnreadableBecause)
{
    /// <summary>A verdict that was read, and says what it is.</summary>
    public static RoutingOutcome Read(RoutingProposal proposal) => new(proposal, null);

    /// <summary>No verdict, and the reason there is none. The work item parks on this.</summary>
    public static RoutingOutcome Unreadable(string because) => new(null, because);

    public bool IsReadable => Proposal is not null;
}

/// <summary>
/// Reads a routing round's verdict out of <c>routing.json</c>, and is the only reader of
/// that file's shape in the process.
/// </summary>
/// <remarks>
/// <para>
/// It behaves like <see cref="Results.RoundResultDeriver"/> for the same reason and with the
/// same rules: it observes, it never asks the agent anything, and it has no way to fail.
/// Every entry point returns a <see cref="RoutingOutcome"/>, because a factory that dropped a
/// work item on the floor for want of a parse would report a routing round as a routing round
/// that decided nothing.
/// </para>
/// <para>
/// <strong>Unreadable is not the same as empty.</strong> A file that is missing, truncated,
/// written in a shape this build does not know, or carrying a verdict that is not one of the
/// three is <see cref="RoutingOutcome.Unreadable"/>, and it is what makes the work item park
/// saying why. A verdict this build does not recognise is deliberately <em>not</em> read as
/// <see cref="WorkItemKind.Build"/>: a default that builds things is the one default that
/// spends a container on work nobody decided should be spent.
/// </para>
/// <para>
/// The dependency on <see cref="WorkItemKind"/> is the one coupling worth naming. The file
/// says <c>Build</c>, <c>Grill</c> or <c>Wayfinder</c> in words, and this reader turns those
/// words into the enum the state machine uses, so the vocabulary of the skills and the
/// vocabulary of the loop are reconciled in exactly one place rather than being spelled out
/// in every caller.
/// </para>
/// </remarks>
public sealed class RoutingReader
{
    /// <summary>The only file shape this build reads. A verdict in any other is unreadable.</summary>
    public const string Schema = "agent-factory/routing@1";

    /// <summary>
    /// The verdict, or why there is not one. Never throws and never guesses: a missing file,
    /// a file that is not JSON, a file this build's schema does not cover, a verdict that is
    /// not one of the three, and a verdict that promises a payload it did not write all
    /// arrive as the second thing.
    /// </summary>
    public RoutingOutcome Read(string? path)
    {
        if (path is not { Length: > 0 } || !File.Exists(path))
        {
            return RoutingOutcome.Unreadable(
                "the round wrote no routing verdict, so nothing has said what kind of work this is");
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return RoutingOutcome.Unreadable(
                $"the round's routing verdict could not be read off this host: {failure.Message}");
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(text);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return RoutingOutcome.Unreadable(
                "the round's routing verdict is not valid JSON, so it was truncated or malformed");
        }

        return Read(root);
    }

    private static RoutingOutcome Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return RoutingOutcome.Unreadable("the round's routing verdict is not a record this build can read");
        }

        // The schema is checked before anything else is looked at, and a mismatch is not
        // read leniently. A verdict from a future build of the image may carry fields this
        // one would silently drop, and a verdict half-understood is worse than one refused:
        // the fields that would be dropped are the ones a newer round relies on.
        if (Text(root, "schema") != Schema)
        {
            return RoutingOutcome.Unreadable(
                "the round wrote a routing verdict in a shape this build does not read");
        }

        var verdict = Text(root, "verdict") switch
        {
            "Build" => WorkItemKind.Build,
            "Grill" => WorkItemKind.Grill,
            "Wayfinder" => WorkItemKind.Wayfinder,
            _ => (WorkItemKind?)null,
        };

        if (verdict is null)
        {
            return RoutingOutcome.Unreadable(
                "the round's routing verdict is not one of the three the factory knows how to act on");
        }

        // The reason is the round's own words and the card's only account of why the verdict
        // is what it is. A verdict with no reason is a verdict a reviewer cannot check, so it
        // is refused here rather than allowed through to be refused again by the store — this
        // way the reason names the file rather than the work item.
        if (Text(root, "because") is not { Length: > 0 } because)
        {
            return RoutingOutcome.Unreadable(
                "the round gave a verdict and no reason for it, and a verdict nobody can check is not one");
        }

        var questions = List(root, "questions")
            .Select(question => new ProposedQuestion(
                Text(question, "question") ?? string.Empty,
                Text(question, "because") ?? string.Empty))
            .Where(question => question.Question.Length > 0)
            .ToList();

        // A `Wayfinder` that charted no map, and a `Grill` that asked nothing, both leave a
        // work item waiting on something that does not exist. Both are refused rather than
        // published, because "the way is not visible" with an empty map is a round that
        // reported a problem without doing anything about it — which is precisely the shape
        // this whole workflow exists to stop.
        if (verdict == WorkItemKind.Wayfinder)
        {
            var map = ReadMap(root);
            return map is null
                ? RoutingOutcome.Unreadable(
                    "the round said the way to the destination is not visible and then charted no map")
                : RoutingOutcome.Read(new RoutingProposal(verdict.Value, because, questions, map));
        }

        if (verdict == WorkItemKind.Grill && questions.Count == 0)
        {
            return RoutingOutcome.Unreadable(
                "the round said this needs grilling and then asked nothing, so there is no question to answer");
        }

        return RoutingOutcome.Read(new RoutingProposal(verdict.Value, because, questions, null));
    }

    private static ProposedMap? ReadMap(JsonElement root)
    {
        if (root.TryGetProperty("map", out var map) is false || map.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var destination = Text(map, "destination");
        if (destination is not { Length: > 0 })
        {
            return null;
        }

        var tickets = List(map, "tickets")
            .Select(ticket => new ProposedTicket(
                Text(ticket, "title") ?? string.Empty,
                Text(ticket, "body") ?? string.Empty,
                Lines(ticket, "blocks")))
            .Where(ticket => ticket.Title.Length > 0)
            .ToList();

        return new ProposedMap(
            destination,
            Text(map, "notes") ?? string.Empty,
            Lines(map, "notYetSpecified"),
            Lines(map, "outOfScope"),
            tickets);
    }

    private static List<string> Lines(JsonElement owner, string field) =>
        List(owner, field)
            .Select(line => line.ValueKind == JsonValueKind.String ? line.GetString() : null)
            .Where(line => line is { Length: > 0 })
            .Select(line => line!)
            .ToList();

    private static IEnumerable<JsonElement> List(JsonElement owner, string field) =>
        owner.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    private static string? Text(JsonElement owner, string field) =>
        owner.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}