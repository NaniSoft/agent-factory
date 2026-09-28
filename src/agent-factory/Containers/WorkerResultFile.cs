namespace AgentFactory.Containers;

using System.Text.Json;

/// <summary>
/// The round's one result file, read for the two things the factory needs that are not a
/// derivation: the header, which is the round's own account of how it was run, and the
/// agent's one optional sentence.
/// </summary>
/// <remarks>
/// <para>
/// Everything else in that file is read by the result deriver, and that separation is
/// deliberate rather than incidental. The deriver lives in <c>Results/</c> and owns the
/// records — files changed, commands run, outcomes, the diff — because ADR-0011's whole
/// claim is that those are derived by observing rather than authored, and a reader that
/// sits beneath the deriver and hands it a header line is a second reading of the same file
/// to keep in step. This class reads the header because a round's identity is worth having
/// on a log line even when nothing else about it can be read (story 29), and the note
/// because it is the only part a model wrote and it is optional by construction.
/// </para>
/// <para>
/// The image writes JSON lines and guarantees the first is the header, which is what makes
/// a truncated or unreadable result still carry the round's identity rather than being
/// nothing at all.
/// </para>
/// </remarks>
public static class WorkerResultFile
{
    /// <summary>
    /// The header line, verbatim, or null when the file has no line this can read. A round
    /// with an unreadable result is still a round that came back, and saying so is better
    /// than dropping the round on the floor.
    /// </summary>
    /// <remarks>
    /// The line is returned as written rather than parsed into a shape, because nothing on
    /// this side of the boundary is entitled to an opinion about it. A header that gains a
    /// field is then a log line that changes on its own rather than a parse that has to be
    /// taught what the field means.
    /// </remarks>
    public static string? ReadHeader(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            return line;
        }

        return null;
    }

    /// <summary>
    /// The agent's one optional sentence, or null. It is the only part of the result a
    /// model authored, so it is read raw and never required: a round that omits it loses a
    /// sentence, not its record. Lines are matched as text before being parsed, because a
    /// round's result is mostly a diff and none of that needs a JSON reader.
    /// </summary>
    public static string? ReadNote(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        foreach (var line in File.ReadLines(path))
        {
            if (!line.Contains("\"kind\":\"note\"", StringComparison.Ordinal))
            {
                continue;
            }

            if (Text(line, "text") is { } note)
            {
                return note;
            }
        }

        return null;
    }

    private static string? Text(string line, string field)
    {
        try
        {
            using var record = JsonDocument.Parse(line);
            return record.RootElement.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
        catch (JsonException)
        {
            // A line that is not JSON is a line that is not the one being looked for.
            return null;
        }
    }
}
