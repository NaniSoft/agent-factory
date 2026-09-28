namespace AgentFactory.Containers;

using System.Text.Json;

/// <summary>
/// The round's one result file, read at the shallowest level the factory reads anything.
/// The image writes JSON lines and guarantees the first is the header, which is what
/// makes a truncated or unreadable result still carry the round's identity rather than
/// being nothing at all (story 29).
/// </summary>
/// <remarks>
/// This is deliberately not a result deriver. What a round's records mean — which files
/// changed, which commands passed, what a test outcome implies — is the deriver's job
/// (ADR-0011) and it has not been written: this ticket delivers the boundary, and the
/// payload it produces is thin on purpose. What is read here is a header line, verbatim,
/// and the one optional sentence the agent is allowed to contribute.
/// </remarks>
public static class WorkerResultFile
{
    /// <summary>
    /// The header, or null when the file has no line this can read. A round with an
    /// unreadable result is still a round that came back, and saying so is better than
    /// dropping the round on the floor.
    /// </summary>
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
