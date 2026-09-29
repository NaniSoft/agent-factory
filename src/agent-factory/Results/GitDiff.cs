namespace AgentFactory.Results;

using System.Text;

/// <summary>
/// Git's own diff format, read in one place. Two things read a diff in this factory —
/// the deriver, which takes the container's copy out of the result file, and the review
/// surface, which renders the host's copy one file at a time — and they must not be two
/// parsers that can disagree about what a section says.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here interprets a diff. It splits one into the sections git wrote, names the
/// path each section is about, and counts the lines it adds and removes. A round's change
/// is what git says it is, and the board's job is to show that rather than to decide
/// which of it matters (ADR-0011).
/// </para>
/// <para>
/// A binary change has no <c>---</c>/<c>+++</c> pair and no line counts, and it is still a
/// change. So is a pure rename, which has no content at all. Both are named here so that
/// neither reads as a section with nothing in it.
/// </para>
/// </remarks>
public static class GitDiff
{
    /// <summary>The line git starts every file's section with.</summary>
    public const string Header = "diff --git ";

    /// <summary>
    /// One section per file. Everything before the first <c>diff --git</c> header is a
    /// preamble with no file in it and is not a section, because a path invented out of a
    /// preamble is a path that does not exist.
    /// </summary>
    public static IReadOnlyList<string> Sections(string? diff)
    {
        if (string.IsNullOrEmpty(diff))
        {
            return [];
        }

        var sections = new List<string>();
        StringBuilder? current = null;

        foreach (var line in Normalise(diff).Split('\n'))
        {
            if (line.StartsWith(Header, StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    sections.Add(current.ToString());
                }

                current = new StringBuilder(line);
                continue;
            }

            current?.Append('\n').Append(line);
        }

        if (current is not null)
        {
            sections.Add(current.ToString());
        }

        return sections;
    }

    /// <summary>
    /// The path a header line inside one section names, with git's <c>a/</c>/<c>b/</c>
    /// prefixes and quoting removed. Null when the line is not there, is empty, or is
    /// <c>/dev/null</c> — which is how git says that side of the diff does not exist, and
    /// reporting it would put a file in a reviewer's list that is not one.
    /// </summary>
    public static string? PathFrom(string section, string header)
    {
        foreach (var raw in section.Split('\n'))
        {
            if (!raw.StartsWith(header, StringComparison.Ordinal))
            {
                continue;
            }

            var path = raw[header.Length..].Trim();
            if (path.Length == 0 || path is "/dev/null")
            {
                return null;
            }

            return Unquote(Strip(path));
        }

        return null;
    }

    /// <summary>
    /// The path a <c>diff --git</c> line names. Used only where a section has no other
    /// header: a binary change, where git writes neither <c>---</c> nor <c>+++</c>.
    /// </summary>
    /// <remarks>
    /// The line's shape is <c>diff --git a/&lt;from&gt; b/&lt;to&gt;</c>, and a path may
    /// contain a space, so the "b/" is found from the right rather than by splitting on
    /// it: a path called <c>b/src/x</c> must not have its own first segment mistaken for
    /// the prefix. The prefix is stripped from whichever side it is genuinely on, so a
    /// file called <c>a/x</c> at the repository root survives.
    /// </remarks>
    public static string? HeaderPath(string section)
    {
        var line = section.Split('\n')[0];
        if (!line.StartsWith(Header, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = line[Header.Length..];
        var middle = rest.IndexOf(" b/", StringComparison.Ordinal);
        return middle <= 0 ? null : Unquote(Strip(rest[(middle + 3)..]));
    }

    /// <summary>
    /// What git said happened to the file this section is about. Read from the section's
    /// own headers, so it is a fact about the diff rather than a guess from its shape.
    /// </summary>
    public static FileChange Change(string section)
    {
        if (section.Contains("new file mode", StringComparison.Ordinal))
        {
            return FileChange.Added;
        }

        if (section.Contains("deleted file mode", StringComparison.Ordinal))
        {
            return FileChange.Deleted;
        }

        if (section.Contains("rename from", StringComparison.Ordinal))
        {
            return FileChange.Renamed;
        }

        if (section.Contains("copy from", StringComparison.Ordinal))
        {
            return FileChange.Copied;
        }

        if (section.Contains("old mode", StringComparison.Ordinal) && section.Contains("new mode", StringComparison.Ordinal))
        {
            return FileChange.TypeChanged;
        }

        // A content change, and a binary one too: git writes a binary diff with no
        // `---`/`+++` pair and no other header to read, and it is still a change.
        return FileChange.Modified;
    }

    /// <summary>
    /// The lines a section adds and removes. The <c>---</c> and <c>+++</c> file headers
    /// are not content, and <c>\ No newline</c> is neither, so neither is counted.
    /// </summary>
    public static (int Added, int Removed) Counts(string section)
    {
        var added = 0;
        var removed = 0;

        foreach (var line in section.Split('\n'))
        {
            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith('+'))
            {
                added++;
            }
            else if (line.StartsWith('-'))
            {
                removed++;
            }
        }

        return (added, removed);
    }

    /// <summary>
    /// Whether git wrote this section as a binary one. It has no readable content, which
    /// is a fact about the change rather than a gap in the record, and the review surface
    /// says so on the line rather than showing an empty body.
    /// </summary>
    public static bool IsBinary(string section) =>
        section.Contains("Binary files ", StringComparison.Ordinal)
        || section.Contains("GIT binary patch", StringComparison.Ordinal);

    /// <summary>
    /// Where a file's change came from, when git says it moved or was copied. The line is
    /// a bare path rather than a prefixed one, so it is not <see cref="Strip"/>ped: a file
    /// called <c>a/x</c> at the repository root keeps its name when it moves.
    /// </summary>
    public static string? PreviousPathOf(string section) =>
        PathFrom(section, "rename from ") ?? PathFrom(section, "copy from ");

    /// <summary>
    /// The path a section is about, in the order the observations are trusted. The
    /// <c>+++</c> side is the file as it now is and is what a reviewer is judging, so it
    /// comes first; the <c>---</c> side is the fallback; a rename or copy names its
    /// destination explicitly; and the <c>diff --git</c> line is the last resort, for a
    /// binary change where git wrote nothing else.
    /// </summary>
    public static string? PathOf(string section) =>
        PathFrom(section, "+++ ")
        ?? PathFrom(section, "--- ")
        ?? PathFrom(section, "rename to ")
        ?? PathFrom(section, "copy to ")
        ?? HeaderPath(section);

    /// <summary>Git's <c>a/</c> and <c>b/</c> path prefixes, removed.</summary>
    public static string Strip(string path) =>
        path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal)
            ? path[2..]
            : path;

    /// <summary>
    /// A path as git wrote it. Git quotes a path containing a space, a quote or anything
    /// above ASCII, and escapes those characters inside the quotes; a path that arrives
    /// quoted and is left quoted names a file that does not exist.
    /// </summary>
    public static string Unquote(string path)
    {
        if (path.Length < 2 || path[0] != '"' || path[^1] != '"')
        {
            return path;
        }

        var inner = path[1..^1];
        var unescaped = new StringBuilder(inner.Length);
        for (var at = 0; at < inner.Length; at++)
        {
            if (inner[at] != '\\' || at + 1 >= inner.Length)
            {
                unescaped.Append(inner[at]);
                continue;
            }

            at++;
            unescaped.Append(inner[at] switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                '\\' => '\\',
                '"' => '"',
                var other => other,
            });
        }

        return unescaped.ToString();
    }

    /// <summary>
    /// One set of line endings, whatever the round's container wrote. A diff lifted out of
    /// a Linux tree and read on a Windows host arrives with both, and a section split on
    /// the wrong one leaves a stray carriage return on every line of a reviewer's screen.
    /// </summary>
    public static string Normalise(string diff) =>
        diff.Contains('\r') ? diff.Replace("\r\n", "\n") : diff;
}
