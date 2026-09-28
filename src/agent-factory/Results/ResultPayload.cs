namespace AgentFactory.Results;

using System.Globalization;
using System.Text;

/// <summary>
/// The derived result rendered as the text a reviewer reads on the board. It is a
/// rendering and nothing more: the structured <see cref="DerivedResult"/> is the record,
/// and this is the one honest way to put all of it in front of somebody at once without
/// the board taking a view of what any of it means.
/// </summary>
/// <remarks>
/// <para>
/// The order is a reviewer's order, not the file's: what the round was, what it changed,
/// what it ran and what came back, the diff, and the agent's own sentence last — because
/// the sentence is the only thing here a model wrote and it is worth the least.
/// </para>
/// <para>
/// A result that is not a record renders as the reason it is not, followed by the round's
/// log. That is story 29 in one method: a round whose result is malformed, truncated or
/// absent still tells a reviewer what happened, rather than rendering an empty card that
/// reads as a round which changed nothing.
/// </para>
/// </remarks>
public static class ResultPayload
{
    /// <summary>
    /// The whole result, as the board shows it. Never throws and never returns nothing:
    /// a result with no files, no commands and no diff says so explicitly, because an
    /// empty string on a card reads as a round that produced nothing rather than as a
    /// round that produced nothing observable.
    /// </summary>
    public static string Of(DerivedResult result, string? log = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        var text = new StringBuilder();

        if (!result.IsARecord)
        {
            text.Append("this round's result could not be read: ").AppendLine(result.UnreadableBecause);
            text.AppendLine().AppendLine("the round's log ends:");

            foreach (var line in (log ?? string.Empty).Split('\n'))
            {
                text.Append(line.TrimEnd('\r')).AppendLine();
            }

            return text.ToString().TrimEnd() + "\n";
        }

        Headline(result, text);
        Files(result, text);
        Commands(result, text);
        Diff(result, text);
        Note(result, text);
        Limits(result, text);

        return text.ToString().TrimEnd() + "\n";
    }

    private static void Headline(DerivedResult result, StringBuilder text)
    {
        var round = result.Environment;

        text.Append("result  ").AppendLine(RoundResultDeriver.WorkerResultSchema);
        text.Append("round   ran as ")
            .Append(round.User is { Length: > 0 } user ? $"{user} (uid {round.Uid})" : $"uid {round.Uid}")
            .AppendLine();
        text.Append("tree    ")
            .AppendLine(round.IsARepository
                ? $"{round.Branch ?? "(no branch)"} at {(Short(round.Head) ?? "no commit")}, from {(Short(round.StartHead) ?? "no commit")}"
                : "was not a git repository, so there is no diff to judge");

        if (result.Commits.Count > 0)
        {
            text.AppendLine();
            text.Append("commits made by the round").AppendLine();
            foreach (var commit in result.Commits)
            {
                text.Append("  ").Append(Short(commit.Id)).Append(' ').AppendLine(commit.Subject);
            }
        }

        // ADR-0006. The image records the *names* of credential-shaped variables and
        // never their values, so this line is the evidence a reviewer can check rather
        // than a claim the board repeats, and the remotes below show where the tree came
        // from and whether that URL carried a credential of its own.
        text.AppendLine();
        text.Append("credentials the round was handed: ")
            .AppendLine(round.CredentialNames.Count == 0
                ? "none"
                : string.Join(", ", round.CredentialNames) + " (by name; no value is ever recorded)");
        text.Append("remotes  ").AppendLine(round.Remotes.Trim() is { Length: > 0 } remotes
            ? remotes.Replace("\n", " ").Replace("\r", string.Empty)
            : "none, so there is nowhere for the round to push");
    }

    private static void Files(DerivedResult result, StringBuilder text)
    {
        text.AppendLine().AppendLine("files changed, from git");
        if (result.FilesChanged.Count == 0)
        {
            text.AppendLine("  none: git saw the tree exactly as the round found it");
            return;
        }

        foreach (var file in result.FilesChanged)
        {
            text.Append("  ")
                .Append(Word(file.Change).PadRight(9))
                .Append(file.Path);

            if (file.PreviousPath is { Length: > 0 } from)
            {
                text.Append(" (from ").Append(from).Append(')');
            }

            text.Append("  ").Append(file.InTheDiff
                ? $"+{file.Added} -{file.Removed}"
                : "not in the diff, so it is on disk and uncommitted");

            if (file.InTheDiff && file.Uncommitted)
            {
                text.Append("; still uncommitted");
            }

            text.AppendLine();
        }
    }

    private static void Commands(DerivedResult result, StringBuilder text)
    {
        text.AppendLine().AppendLine("commands run, and what each returned");
        if (result.CommandsRun.Count == 0)
        {
            text.AppendLine("  none were recorded: this is the recording wrapper's limit, not a round that ran nothing");
            return;
        }

        foreach (var command in result.CommandsRun)
        {
            text.Append("  ")
                .Append((command.Passed ? "exit 0" : $"exit {command.ExitCode}").PadRight(9))
                .Append(command.DurationMs is { } ms
                    ? $"{(ms / 1000d).ToString("0.0", CultureInfo.InvariantCulture)}s  "
                    : "        ")
                .AppendLine(command.Label is { Length: > 0 } label ? label : "(unlabelled)");
            text.Append("            ").AppendLine(command.Command);

            // The end of what a command said, which is where a failing build says why.
            foreach (var tail in new[] { command.StdoutTail, command.StderrTail })
            {
                foreach (var line in tail.Split('\n').Take(TailLines))
                {
                    if (line.Trim().Length > 0)
                    {
                        text.Append("            │ ").AppendLine(line.TrimEnd('\r'));
                    }
                }
            }
        }

        text.AppendLine("  the factory records what ran and what it returned; it does not decide what any of it meant");
    }

    private static void Diff(DerivedResult result, StringBuilder text)
    {
        text.AppendLine().AppendLine("the diff, from the commit the round started at");
        if (result.Diff.Trim().Length == 0)
        {
            text.AppendLine("  empty: nothing on disk differs from the commit the round started at");
            return;
        }

        foreach (var line in result.Diff.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
        {
            text.AppendLine(line);
        }

        if (result.DiffTruncated)
        {
            text.AppendLine("(the image bounded this diff; the whole one is in the round's result file)");
        }
    }

    private static void Note(DerivedResult result, StringBuilder text)
    {
        text.AppendLine().AppendLine("the agent's own account, which nothing above is derived from");
        if (result.AgentNote is not { Length: > 0 } note)
        {
            text.AppendLine("  it wrote none, and the round is recorded whole without one");
            return;
        }

        foreach (var line in note.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
        {
            text.Append("  ").AppendLine(line);
        }
    }

    /// <summary>
    /// What the factory could not read, said plainly at the end rather than left for a
    /// reader to infer from a shorter file. The design names this failure — "a results
    /// payload that parses but is missing the field the reviewer needs" — and says only
    /// integration tests catch it, so the count is carried to the board rather than
    /// dropped here.
    /// </summary>
    private static void Limits(DerivedResult result, StringBuilder text)
    {
        if (result.UnreadableLines == 0)
        {
            return;
        }

        text.AppendLine()
            .AppendLine($"note    {result.UnreadableLines} line(s) of the result file were not records the factory could "
                + "read, so this is a partial result and says so rather than reading as a whole one");
    }

    private static string Word(FileChange change) => change switch
    {
        FileChange.Added => "added",
        FileChange.Deleted => "deleted",
        FileChange.Renamed => "renamed",
        FileChange.Copied => "copied",
        FileChange.TypeChanged => "mode",
        FileChange.Unmerged => "unmerged",
        FileChange.Untracked => "untracked",
        _ => "modified",
    };

    private static string? Short(string? commit) =>
        commit is { Length: > 7 } ? commit[..7] : commit is { Length: > 0 } ? commit : null;

    /// <summary>How much of one command's output the payload carries.</summary>
    private const int TailLines = 6;
}
