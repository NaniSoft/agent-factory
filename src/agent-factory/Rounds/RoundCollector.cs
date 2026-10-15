namespace AgentFactory.Rounds;

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentFactory.Results;

/// <summary>
/// What a round left behind, gathered into the one result file the factory reads. This is
/// the host-side successor of the worker image's collector, and it writes the same file in
/// the same shape: JSON <em>lines</em>, one self-contained object per line, the header
/// always first (schema <see cref="RoundResultDeriver.WorkerResultSchema"/>).
/// </summary>
/// <remarks>
/// <para>
/// Everything here is an <em>observation</em>, and nothing asks the agent anything
/// (ADR-0011): the files git saw, the commands the recording shim ran and what each
/// returned, and the agent's one optional sentence copied raw. The git reads are pinned the
/// way the diff reader's are — <c>core.autocrlf=false</c>, <c>core.quotepath=false</c>,
/// <c>safe.directory</c> through the environment — because a tree read with the host's own
/// line-ending setting would record bytes the round never wrote (#26).
/// </para>
/// <para>
/// The result path is outside the working tree, exactly as it was when the tree lived in a
/// container: a result written inside the tree would show up in the change a reviewer is
/// judging. The landing directory is the round's <c>out</c>; the tree is the round's
/// <c>work</c>; nothing in the collector writes to the tree.
/// </para>
/// <para>
/// The file is created and truncated before the first record, and every record is appended
/// — the same ordering argument the image's collector made, for the same reason: a collector
/// that fell over mid-way leaves a partial file, which the reader degrades gracefully, and
/// not a half-written line that costs everything after it.
/// </para>
/// </remarks>
public static class RoundCollector
{
    /// <summary>How much of one git observation is kept. A tail, not an error: the whole change is in the tree.</summary>
    public const int GitTailBytes = 1024 * 1024;

    /// <summary>Where the recording shim leaves its command log, inside the landing directory.</summary>
    public const string CommandLogFileName = "commands.ndjson";

    /// <summary>The brief file, as the agent was told to read it.</summary>
    public const string BriefFileName = "brief.md";

    /// <summary>The agent's optional note, as the brief asked it to write.</summary>
    public const string NoteFileName = "note.md";

    /// <summary>The result file, as the deriver and the store read it.</summary>
    public const string ResultFileName = "result.json";

    /// <summary>
    /// Writes the round's result file and returns where it landed, or null when nothing
    /// could be written. Never throws for a tree it cannot read: a round whose observations
    /// fail one by one still gets the header, which is what makes a round that produced
    /// nothing else still not invisible.
    /// </summary>
    /// <param name="landing">The round's out directory: result, brief, note, command log.</param>
    /// <param name="workDirectory">The tree the round worked in, when it was given one.</param>
    /// <param name="startHead">The commit captured before the agent touched anything, or null.</param>
    /// <param name="roundExitCode">
    /// The exit code of the round's own last command — the agent, since the child this
    /// factory starts *is* the agent. Null says the round has not said how it ended, and an
    /// absent field is carried as an absence rather than read as a zero (#22).
    /// </param>
    /// <param name="credentialEnvNames">
    /// The names of the credential-shaped variables the round was handed — names, never
    /// values. This is the evidence a reviewer checks rather than a claim the board repeats
    /// (ADR-0006).
    /// </param>
    public static string? Collect(
        string landing,
        string workDirectory,
        string? startHead,
        int? roundExitCode,
        IReadOnlyList<string> credentialEnvNames)
    {
        var began = Stopwatch.GetTimestamp();
        var result = Path.Combine(landing, ResultFileName);

        try
        {
            using var stream = new FileStream(
                result,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));

            if (Header(landing, workDirectory, startHead, roundExitCode, credentialEnvNames) is { } header)
            {
                writer.WriteLine(header);
            }

            // The commands, verbatim from the recording shim. Copied rather than re-derived:
            // the shim observed them while they ran, and re-running anything here could only
            // observe the round twice.
            var log = Path.Combine(landing, CommandLogFileName);
            if (File.Exists(log))
            {
                foreach (var line in File.ReadLines(log))
                {
                    if (line.Length > 0)
                    {
                        writer.WriteLine(line);
                    }
                }
            }

            foreach (var observation in GitObservations(workDirectory, startHead))
            {
                writer.WriteLine(observation);
            }

            var note = Path.Combine(landing, NoteFileName);
            if (File.Exists(note))
            {
                var text = File.ReadAllText(note);
                if (text.Length > 0)
                {
                    writer.WriteLine(JsonLine(new NoteRecord(
                        "note", "note", Encoding.UTF8.GetByteCount(text), text)));
                }
            }

            var commandRecords = 0;
            if (File.Exists(log))
            {
                foreach (var line in File.ReadLines(log))
                {
                    if (line.Contains("\"kind\":\"command\"", StringComparison.Ordinal))
                    {
                        commandRecords++;
                    }
                }
            }

            var collectedInMs = (long)Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            writer.WriteLine(JsonLine(new EndRecord("end", collectedInMs, commandRecords)));

            writer.Flush();
            return result;
        }
        catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Line 1, always. The host reads the header first and says so when it is missing, and
    /// a round that observed nothing else is still a round whose exit code, tree and
    /// credentials are on the record.
    /// </summary>
    private static string? Header(
        string landing,
        string workDirectory,
        string? startHead,
        int? roundExitCode,
        IReadOnlyList<string> credentialEnvNames)
    {
        var isRepo = Directory.Exists(Path.Combine(workDirectory, ".git"));
        var head = isRepo ? GitOutput(workDirectory, ["rev-parse", "HEAD"]) : null;
        var branch = isRepo ? GitOutput(workDirectory, ["rev-parse", "--abbrev-ref", "HEAD"]) : null;

        return JsonLine(new HeaderRecord(
            Kind: "result",
            Schema: RoundResultDeriver.WorkerResultSchema,
            RoundExitCode: roundExitCode,
            Work: workDirectory,
            Out: landing,
            GitRepo: isRepo,
            Head: head,
            Branch: branch,
            StartHead: startHead,
            User: Environment.UserName,
            Uid: -1,
            CredentialEnvNames: credentialEnvNames,
            Git: VersionOf("git"),
            Opencode: VersionOf("opencode"),
            Os: Environment.OSVersion.ToString(),
            Kernel: Environment.OSVersion.VersionString));
    }

    /// <summary>
    /// The git observations a tree owes a reviewer: the status (which catches the
    /// uncommitted file the diff misses), the diff against the commit the round started at
    /// (which catches the committed work the status misses), the commits since that start,
    /// and the remotes — which is where "this round had nowhere to push" becomes checkable.
    /// When the start could not be read, the staged and unstaged halves are recorded
    /// separately rather than concatenated, so a reader can tell which comparison they are
    /// looking at.
    /// </summary>
    private static IEnumerable<string> GitObservations(string workDirectory, string? startHead)
    {
        if (!Directory.Exists(Path.Combine(workDirectory, ".git")))
        {
            yield break;
        }

        yield return GitRecord(workDirectory, "status", ["status", "--porcelain=v2", "--untracked-files=all"]);

        if (GitOutput(workDirectory, ["rev-parse", "HEAD"]) is not { Length: > 0 } head)
        {
            yield break;
        }

        var baseCommit = startHead is { Length: > 0 } start ? start : head;

        if (startHead is { Length: > 0 })
        {
            yield return GitRecord(workDirectory, "diffFromRoundStart", DiffArguments(baseCommit));
            yield return GitRecord(workDirectory, "commitsSinceRoundStart", ["log", "--format=%H %s", $"{baseCommit}..HEAD"]);
        }
        else
        {
            yield return GitRecord(workDirectory, "diffStaged", DiffArguments("--cached"));
            yield return GitRecord(workDirectory, "diffUnstaged", DiffArguments(null));
        }

        yield return GitRecord(workDirectory, "remotes", ["remote", "-v"]);
    }

    /// <summary>
    /// The arguments a diff is read with: the same pins the board's own diff reader makes,
    /// because both observations of one change must be functions of the round's bytes and
    /// not of the machine reading them.
    /// </summary>
    private static IReadOnlyList<string> DiffArguments(string? against)
    {
        var arguments = new List<string>
        {
            "-c", "core.quotepath=false",
            "-c", "core.autocrlf=false",
            "--no-pager", "diff", "--no-color", "--no-ext-diff", "--no-textconv",
        };

        if (against is { Length: > 0 })
        {
            arguments.Add(against);
        }

        return arguments;
    }

    /// <summary>One git observation, as the reader's own <c>git</c> record: bytes, bound, text.</summary>
    private static string GitRecord(string workDirectory, string field, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workDirectory,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // The same safe.directory the diff reader passes, through the environment, for the
        // same reason: reading a round's observations must never modify a round's tree.
        foreach (var (name, value) in Results.HostDiffReader.GitEnvironment)
        {
            start.Environment[name] = value;
        }

        try
        {
            using var process = new Process { StartInfo = start };
            if (!process.Start())
            {
                return EmptyGitRecord(field);
            }

            process.StandardInput.Close();
            var text = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            // The stderr is named when the observation failed, because an empty record
            // that says nothing about why is the one silence a reviewer cannot act on —
            // "git saw nothing" and "git could not look" must not read the same.
            return process.ExitCode == 0
                ? JsonLine(new GitObservation("git", field, Encoding.UTF8.GetByteCount(text), text.Length > GitTailBytes, Tail(text, GitTailBytes)))
                : JsonLine(new GitObservation("git", field, 0, false, $"git exited {process.ExitCode}: {error.Trim()}"));
        }
        catch (Exception failed) when (failed is Win32Exception or IOException)
        {
            return JsonLine(new GitObservation("git", field, 0, false, $"git could not be started: {failed.Message}"));
        }
    }

    private static string EmptyGitRecord(string field) =>
        JsonLine(new GitObservation("git", field, 0, false, string.Empty));

    /// <summary>
    /// One quick version probe, or null. Recorded for the reviewer, read by nobody's
    /// policy. Cached, because the answer cannot change while this process runs and a
    /// probe per round would be a spawn per round; the argument matters, because a bare
    /// <c>opencode</c> with none is its interactive session and not an answer.
    /// </summary>
    private static readonly Dictionary<string, string?> Versions = new(StringComparer.Ordinal);

    private static string? VersionOf(string executable)
    {
        lock (Versions)
        {
            if (Versions.TryGetValue(executable, out var known))
            {
                return known;
            }
        }

        var version = Probe(executable);
        lock (Versions)
        {
            Versions[executable] = version;
        }

        return version;
    }

    private static string? Probe(string executable)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    ArgumentList = { "--version" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!process.Start())
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            if (!process.HasExited)
            {
                // A probe that hangs is killed, whole tree and all: it is a question
                // about a version, and no round waits on it.
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
                return null;
            }

            return process.ExitCode == 0
                ? output.Split('\r', '\n').FirstOrDefault(line => line.Trim().Length > 0)?.Trim()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string GitOutput(string workDirectory, IReadOnlyList<string> arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = workDirectory,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            foreach (var (name, value) in Results.HostDiffReader.GitEnvironment)
            {
                process.StartInfo.Environment[name] = value;
            }

            if (!process.Start())
            {
                return string.Empty;
            }

            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(10_000);

            return process.ExitCode == 0 ? output : string.Empty;
        }
        catch (Exception failed) when (failed is Win32Exception or IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>The last <c>bytes</c> of the text, cut on whole characters so a multi-byte character is never split.</summary>
    private static string Tail(string text, int bytes)
    {
        var encoded = Encoding.UTF8;
        var total = encoded.GetByteCount(text);
        if (total <= bytes)
        {
            return text;
        }

        // Walk forward from the front, dropping whole characters, until what remains fits.
        // The bytes are counted incrementally rather than measured per prefix, so the walk
        // is one pass over the text and not a measurement per character.
        var index = 0;
        while (index < text.Length && total > bytes)
        {
            total -= encoded.GetByteCount(text, index, 1);
            index++;
        }

        return text[index..];
    }

    private static string JsonLine<T>(T record) =>
        JsonSerializer.Serialize(record, Source);

    private static readonly JsonSerializerOptions Source = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    // The records, shaped exactly as the image's collector wrote them, because the reader
    // on the other side is the one component whose contract this file exists to keep.

    private sealed record HeaderRecord(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("roundExitCode")] int? RoundExitCode,
        [property: JsonPropertyName("work")] string Work,
        [property: JsonPropertyName("out")] string Out,
        [property: JsonPropertyName("gitRepo")] bool GitRepo,
        [property: JsonPropertyName("head")] string? Head,
        [property: JsonPropertyName("branch")] string? Branch,
        [property: JsonPropertyName("startHead")] string? StartHead,
        [property: JsonPropertyName("user")] string User,
        [property: JsonPropertyName("uid")] int Uid,
        [property: JsonPropertyName("credentialEnvNames")] IReadOnlyList<string> CredentialEnvNames,
        [property: JsonPropertyName("git")] string? Git,
        [property: JsonPropertyName("opencode")] string? Opencode,
        [property: JsonPropertyName("os")] string Os,
        [property: JsonPropertyName("kernel")] string Kernel);

    private sealed record GitObservation(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("field")] string Field,
        [property: JsonPropertyName("byteLength")] int ByteLength,
        [property: JsonPropertyName("truncated")] bool Truncated,
        [property: JsonPropertyName("text")] string Text);

    private sealed record NoteRecord(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("field")] string Field,
        [property: JsonPropertyName("byteLength")] int ByteLength,
        [property: JsonPropertyName("text")] string Text);

    private sealed record EndRecord(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("collectedInMs")] long CollectedInMs,
        [property: JsonPropertyName("commandRecords")] int CommandRecords);
}
