namespace AgentFactory.Results;

using System.Text.Json;

/// <summary>
/// The result deriver. It reads a round's one result file — the file the host lifted out
/// of the container with <c>docker cp</c> (ADR-0010) — and turns the records in it into
/// the result a reviewer judges: the files changed, the commands run, what each returned,
/// and the diff.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0011 is the whole of this class. The image records; the factory reads and
/// reshapes. Files come from <c>git status</c> and <c>git diff</c>, commands come from
/// the recording wrapper, and an outcome is the exit code that was observed. The agent's
/// prose is copied raw and believed about nothing: a model that claims it edited a file
/// it did not edit changes nothing here, because git is what says which files changed.
/// </para>
/// <para>
/// Nothing here interprets a command. There is deliberately no field saying which of a
/// round's commands were its tests, because the factory does not know and must not
/// guess: the repository's own scripts decide what tested means, and the configuration
/// schema has no field in which the factory could record an opinion (ADR-0011).
/// </para>
/// <para>
/// A result file that is malformed, truncated or absent still produces a
/// <see cref="DerivedResult"/>, carrying the reason it is not a record. The caller falls
/// through to the round's log, so a round whose result cannot be read is a round that
/// says what happened rather than a round that shows nothing (story 29).
/// </para>
/// </remarks>
public sealed class RoundResultDeriver
{
    /// <summary>The schema the image's collector writes. Recorded, not enforced.</summary>
    public const string WorkerResultSchema = "agent-factory/worker-result@1";

    private readonly ILogger<RoundResultDeriver> _logger;

    public RoundResultDeriver(ILogger<RoundResultDeriver> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Derives one round's result from the result file lifted out of its container. Never
    /// throws for a result it cannot read: a round that produced nothing usable is a round
    /// the board still has to show.
    /// </summary>
    /// <param name="resultFile">
    /// The file <c>docker cp</c> brought back, or null when the round wrote none. Null is
    /// the same shape as an unreadable file as far as a reviewer is concerned, and both
    /// degrade to the log.
    /// </param>
    /// <param name="log">
    /// The end of what the round said, which is what a result that cannot be read falls
    /// back to.
    /// </param>
    public DerivedResult Derive(string? resultFile, string? log = null)
    {
        if (resultFile is null)
        {
            return Nothing("the round wrote no result file, so there is nothing to observe");
        }

        if (!File.Exists(resultFile))
        {
            return Nothing($"there is no result file at {resultFile} to observe");
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(resultFile);
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return Nothing($"its result file at {resultFile} could not be read: {unreadable.Message}");
        }

        return Read(lines, resultFile, log);
    }

    /// <summary>
    /// A result that is not a record, carrying why. Everything else about a round is
    /// empty on purpose: a diff nobody observed, a file list nobody observed and a
    /// command list nobody observed would read as a round that changed nothing, tested
    /// nothing and ran nothing, which is a different and much worse claim than saying
    /// the result is unreadable.
    /// </summary>
    private static DerivedResult Nothing(string because) => new(
        FilesChanged: [],
        CommandsRun: [],
        Diff: string.Empty,
        DiffTruncated: false,
        AgentNote: null,
        Commits: [],
        Environment: Unseen(),
        UnreadableLines: 0,
        UnreadableBecause: because);

    private static RoundEnvironment Unseen() => new(
        User: null,
        Uid: -1,
        IsARepository: false,
        Branch: null,
        StartHead: null,
        Head: null,
        Remotes: string.Empty,
        CredentialNames: [],
        Git: null,
        Agent: null,
        OperatingSystem: null);

    private DerivedResult Read(string[] lines, string path, string? log)
    {
        var header = default(JsonElement?);
        var commands = new List<CommandOutcome>();
        var note = (string?)null;
        var observations = new Dictionary<string, string>(StringComparer.Ordinal);
        var truncated = new Dictionary<string, bool>(StringComparer.Ordinal);
        var unreadable = 0;

        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (!Record(line, out var record))
            {
                unreadable++;
                continue;
            }

            switch (Text(record, "kind"))
            {
                case "result" when header is null:
                    header = record.Clone();
                    break;

                case "command":
                    commands.Add(CommandFrom(record));
                    break;

                case "note":
                    // Raw, first one wins, never required. A second note is not an error:
                    // the collector writes at most one, and a round that somehow wrote two
                    // has still told the reviewer something.
                    note ??= Text(record, "text");
                    break;

                case "git" when Text(record, "field") is { Length: > 0 } field:
                    observations[field] = Text(record, "text") ?? string.Empty;
                    truncated[field] = Bool(record, "truncated");
                    break;

                default:
                    // A record kind this deriver does not read: a listener, a trailer, or
                    // one a newer image added. Skipping it is the forward-compatible answer
                    // and is not counted as unreadable — an unknown record is not a broken one.
                    break;
            }
        }

        if (header is not { } facts)
        {
            _logger.LogWarning(
                "A round's result at {Path} has no header line in it, so there is nothing to observe. Its log ends: {Log}",
                path,
                log ?? string.Empty);

            return Nothing($"its result file at {path} has no readable header, so there is nothing to observe");
        }

        var diff = observations.GetValueOrDefault(DiffFromRoundStart)
            ?? observations.GetValueOrDefault(DiffStaged, string.Empty) + observations.GetValueOrDefault(DiffUnstaged, string.Empty);

        var derived = new DerivedResult(
            FilesChanged: FilesFrom(observations.GetValueOrDefault(GitStatus), observations.GetValueOrDefault(DiffFromRoundStart) ?? diff),
            CommandsRun: commands,
            Diff: diff,
            DiffTruncated: truncated.GetValueOrDefault(DiffFromRoundStart),
            AgentNote: note,
            Commits: CommitsFrom(observations.GetValueOrDefault(CommitsSinceRoundStart)).ToList(),
            Environment: EnvironmentFrom(facts, observations.GetValueOrDefault(Remotes)),
            UnreadableLines: unreadable,
            UnreadableBecause: null);

        if (unreadable > 0)
        {
            // The design names this failure and says only integration tests catch it: "a
            // results payload that parses but is missing the field the reviewer needs".
            // A result that is missing records is exactly that, so the count is carried
            // into the result and onto the board rather than left for a reader to infer
            // from a shorter file.
            _logger.LogWarning(
                "{Lines} line(s) of a round's result at {Path} were not records this could read. The result is "
                    + "partial: it says so, rather than reading as a whole one.",
                unreadable,
                path);
        }

        return derived;
    }

    private const string DiffFromRoundStart = "diffFromRoundStart";
    private const string DiffStaged = "diffStaged";
    private const string DiffUnstaged = "diffUnstaged";
    private const string GitStatus = "status";
    private const string CommitsSinceRoundStart = "commitsSinceRoundStart";
    private const string Remotes = "remotes";

    // --- the records ---------------------------------------------------------

    private static bool Record(string line, out JsonElement record)
    {
        // A diff is tens of kilobytes on one line, so nothing here filters by looking for
        // a substring: a record is a record when a JSON reader can parse it, whatever it
        // happens to contain. A line the reader refuses is counted by the caller.
        try
        {
            using var parsed = JsonDocument.Parse(line);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                record = default;
                return false;
            }

            record = parsed.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            record = default;
            return false;
        }
    }

    private static CommandOutcome CommandFrom(JsonElement record) => new(
        Sequence: Int(record, "seq") ?? 0,
        Label: Blank(Text(record, "label")),
        Command: string.Join(' ', Strings(record, "argv")),
        WorkingDirectory: Blank(Text(record, "cwd")),
        ExitCode: Int(record, "exitCode") ?? -1,
        DurationMs: Long(record, "durationMs"),
        StdoutTail: Text(record, "stdoutTail") ?? string.Empty,
        StderrTail: Text(record, "stderrTail") ?? string.Empty,
        StdoutBytes: Long(record, "stdoutBytes") ?? 0,
        StderrBytes: Long(record, "stderrBytes") ?? 0);

    private static RoundEnvironment EnvironmentFrom(JsonElement header, string? remotes) => new(
        User: Blank(Text(header, "user")),
        Uid: Int(header, "uid") ?? -1,
        IsARepository: Bool(header, "gitRepo"),
        Branch: Blank(Text(header, "branch")),
        StartHead: Blank(Text(header, "startHead")),
        Head: Blank(Text(header, "head")),
        Remotes: remotes ?? string.Empty,
        CredentialNames: Strings(header, "credentialEnvNames"),
        Git: Blank(Text(header, "git")),
        Agent: Blank(Text(header, "opencode")),
        OperatingSystem: Blank(Text(header, "os")));

    // --- files changed -------------------------------------------------------

    /// <summary>
    /// Every path the round changed, from two git observations that each catch something
    /// the other misses, unioned.
    /// </summary>
    /// <remarks>
    /// This is where ADR-0011's guarantee actually lives, and it is the reason the
    /// deriver never asks the agent anything. A file the round committed is clean in
    /// <c>git status</c> and named in the diff; a scratch file the round left behind is
    /// the reverse. Take the diff alone and the untracked file is invisible; take the
    /// status alone and the committed work is invisible. Take the note and you have
    /// whatever the model felt like writing.
    /// </remarks>
    private static IReadOnlyList<ChangedFile> FilesFrom(string? status, string? diff)
    {
        var files = new Dictionary<string, ChangedFile>(StringComparer.Ordinal);

        foreach (var file in DiffFiles(diff))
        {
            files[file.Path] = file;
        }

        foreach (var (path, change) in StatusFiles(status))
        {
            files[path] = files.TryGetValue(path, out var fromDiff)
                // Both observations saw it, which is the ordinary case for a staged or
                // committed change, and neither contradicts the other.
                ? fromDiff with
                {
                    Change = change,
                    Uncommitted = true,
                }
                : new ChangedFile(
                    path,
                    PreviousPath: null,
                    Change: change,
                    Added: 0,
                    Removed: 0,
                    InTheDiff: false,
                    Uncommitted: true);
        }

        return files.Values
            .OrderByDescending(file => file.InTheDiff)
            .ThenBy(file => file.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The paths a diff names, and what it did to each. Read from the diff itself rather
    /// than from any summary of it, so a path is whatever git wrote.
    /// </summary>
    private static IEnumerable<ChangedFile> DiffFiles(string? diff)
    {
        if (string.IsNullOrEmpty(diff))
        {
            yield break;
        }

        foreach (var section in GitDiff.Sections(diff))
        {
            // A pure rename or copy has no `---`/`+++` pair at all — git writes
            // `rename from`/`rename to` and leaves it there — so the headers are a
            // fallback rather than the only source. A binary change has no headers either,
            // and its `diff --git` line is the only place the path appears. `GitDiff` is
            // where all of that is read, and the review surface reads diffs through the
            // same code, so the two cannot come to disagree about what a section says.
            if (GitDiff.PathOf(section) is not { Length: > 0 } path)
            {
                continue;
            }

            var (added, removed) = GitDiff.Counts(section);

            yield return new ChangedFile(
                path,
                GitDiff.PreviousPathOf(section),
                GitDiff.Change(section),
                added,
                removed,
                InTheDiff: true,
                Uncommitted: false);
        }
    }

    /// <summary>
    /// The paths <c>git status --porcelain=v2 --untracked-files=all</c> saw, and what it
    /// said about each. Read from the status, so a file nobody committed still counts.
    /// </summary>
    private static IEnumerable<(string Path, FileChange Change)> StatusFiles(string? status)
    {
        if (string.IsNullOrEmpty(status))
        {
            yield break;
        }

        foreach (var raw in status.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                // The `# branch.oid` / `# branch.head` headers, which the header record
                // already carries.
                continue;
            }

            // Porcelain v2 is space-separated with the path last, and a path may itself
            // contain spaces, so the split is bounded rather than by every space.
            switch (line[0])
            {
                case '1':
                {
                    // 1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>
                    // Exactly nine fields, and the path is the ninth — which is why the
                    // split is bounded: a path may contain spaces, and a path read one
                    // field early or one field late is a path that does not exist.
                    var fields = line.Split(' ', 9);
                    if (fields.Length == 9)
                    {
                        yield return (GitDiff.Unquote(fields[8]), Staged(fields[1]));
                    }

                    break;
                }

                case '2':
                {
                    // 2 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <X><score> <path><TAB><origPath>
                    //
                    // Eleven fields, and the tenth is the rename status and score fused
                    // together as one token — "R100" is a similarity score, not part of any
                    // path. Reading the path from the ninth index folds it onto the front
                    // of the filename and produces "R100 new/place.cs", a file that does
                    // not exist. The tenth index is the path, tab-separated from where the
                    // file came from.
                    var fields = line.Split(' ', 11);
                    if (fields.Length == 11)
                    {
                        // The renamed path is the one the change is about; the path git
                        // moved from is already in the diff, which is the record that has
                        // the move in it.
                        yield return (GitDiff.Unquote(fields[10].Split('\t')[0]), FileChange.Renamed);
                    }

                    break;
                }

                case 'u':
                {
                    // u <XY> <sub> <m1> <m2> <m3> <mW> <h1> <h2> <h3> <path>
                    var fields = line.Split(' ', 11);
                    if (fields.Length == 11)
                    {
                        yield return (GitDiff.Unquote(fields[10]), FileChange.Unmerged);
                    }

                    break;
                }

                case '?':
                    yield return (GitDiff.Unquote(line[1..].Trim()), FileChange.Untracked);
                    break;

                case '!':
                    // Ignored on purpose, by a rule the repository itself wrote. It is
                    // not a change the round made.
                    break;
            }
        }
    }

    /// <summary>
    /// What a porcelain v2 index/worktree pair says happened. Y is the working tree's
    /// own state and is the later of the two, so it wins where the two disagree.
    /// </summary>
    /// <summary>
    /// What a porcelain v2 index/worktree pair says happened to a path. Y is the working
    /// tree's own state and is the later of the two, so it wins where the two disagree;
    /// X alone is reported only when Y says nothing.
    /// </summary>
    private static FileChange Staged(string xy)
    {
        if (xy.Length < 2)
        {
            return FileChange.Modified;
        }

        foreach (var state in new[] { xy[1], xy[0] })
        {
            switch (state)
            {
                case 'D':
                    return FileChange.Deleted;

                case 'T':
                    return FileChange.TypeChanged;

                case 'A':
                    return FileChange.Added;

                case 'R':
                    return FileChange.Renamed;

                case 'C':
                    return FileChange.Copied;
            }
        }

        return FileChange.Modified;
    }

    private static IEnumerable<Commit> CommitsFrom(string? log)
    {
        if (string.IsNullOrEmpty(log))
        {
            yield break;
        }

        foreach (var raw in log.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // `git log --format='%H %s'`, so the id is the first space-delimited field and
            // the subject is the rest of the line.
            var split = line.IndexOf(' ', StringComparison.Ordinal);
            yield return split < 0
                ? new Commit(line, string.Empty)
                : new Commit(line[..split], line[(split + 1)..]);
        }
    }

    // --- reading fields ------------------------------------------------------

    private static string? Text(JsonElement record, string field) =>
        record.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement record, string field) =>
        record.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static long? Long(JsonElement record, string field) =>
        record.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    private static bool Bool(JsonElement record, string field) =>
        record.TryGetProperty(field, out var value) && value.ValueKind is JsonValueKind.True;

    private static IReadOnlyList<string> Strings(JsonElement record, string field)
    {
        if (!record.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .ToList();
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
