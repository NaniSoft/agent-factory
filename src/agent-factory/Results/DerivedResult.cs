namespace AgentFactory.Results;

/// <summary>
/// What one round produced, as the factory derived it by watching the container rather
/// than by believing the agent (ADR-0011). The three named things are here: the files
/// changed, the commands run, and the outcome each of them returned.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in this type was authored by a model. The files come from <c>git status</c>
/// and <c>git diff</c> as the container ran them, the commands come from the image's
/// recording wrapper, and an outcome is the exit code the command returned. The agent
/// contributes exactly one field — <see cref="AgentNote"/> — and the factory neither
/// requires it nor believes it.
/// </para>
/// <para>
/// <see cref="FilesChanged"/> is a union on purpose, and it is the shape that carries
/// ADR-0011's guarantee. A file the round committed is clean in <c>git status</c> and
/// present in the diff; a file the round left behind uncommitted is the other way
/// round. Reading either observation alone silently drops half of what a reviewer needs
/// to see, and the untracked file is the one an agent leaves by accident.
/// </para>
/// </remarks>
/// <param name="FilesChanged">
/// Every path the round changed, from git and from nowhere else. A path appears here
/// if <c>git status</c> saw it, or the round's diff names it, or both.
/// </param>
/// <param name="CommandsRun">
/// Every command the image's recording wrapper saw, in the order it saw them, with the
/// exit code each returned. This list under-reports on purpose and that limit is known:
/// a command the agent reaches without going through the wrapper — a tool that shells
/// out on its own, a command typed into a shell the agent opened itself — is not in here,
/// which is exactly why <see cref="FilesChanged"/> and <see cref="Diff"/> come from git
/// and carry ADR-0011's guarantee instead of this list.
/// </param>
/// <param name="Diff">
/// The diff the reviewer judges: what is on disk against the commit the round started
/// from, committed or not. Empty when the round produced none or when the container had
/// no repository.
/// </param>
/// <param name="DiffTruncated">
/// Whether the image bounded the diff it recorded. The factory does not silently shorten
/// a diff a reviewer is judging; it says that it is looking at part of one.
/// </param>
/// <param name="AgentNote">
/// The agent's one optional sentence, copied raw. Null when the round wrote none, when
/// it wrote something that is not text, or when the result could not be read at all —
/// and a round missing it is a round missing a sentence, not a round missing a record.
/// </param>
/// <param name="Commits">The commits the round made since it started, if it made any.</param>
/// <param name="Environment">
/// The facts the container observed about itself: who it ran as, what it was holding,
/// and where its tree came from. This is the evidence behind ADR-0006, not a claim
/// about it.
/// </param>
/// <param name="UnreadableLines">
/// How many lines of the result file were not records this could read. Non-zero means
/// the result is partial, and a partial result says so rather than reading as a whole one.
/// </param>
/// <param name="UnreadableBecause">
/// Why the result is not a record at all, or null when it is one. A round whose result
/// cannot be read still has a log, and the board shows that instead of nothing.
/// </param>
public sealed record DerivedResult(
    IReadOnlyList<ChangedFile> FilesChanged,
    IReadOnlyList<CommandOutcome> CommandsRun,
    string Diff,
    bool DiffTruncated,
    string? AgentNote,
    IReadOnlyList<Commit> Commits,
    RoundEnvironment Environment,
    int UnreadableLines,
    string? UnreadableBecause)
{
    /// <summary>Commands that returned something other than zero, in the order they ran.</summary>
    public IReadOnlyList<CommandOutcome> FailedCommands =>
        CommandsRun.Where(command => !command.Passed).ToList();

    /// <summary>
    /// Whether this is a record at all, as opposed to a reason it is not. The board
    /// falls through to the round's log when it is false, so a round is never invisible
    /// (story 29).
    /// </summary>
    public bool IsARecord => UnreadableBecause is null;
}

/// <summary>
/// One file the round changed, and how the factory knows. Every field here was read out
/// of git; none of it came from anything the agent said.
/// </summary>
/// <param name="Path">The path, relative to the repository root, as git reports it.</param>
/// <param name="PreviousPath">Where the file was before, when git says it moved.</param>
/// <param name="Change">What git says happened to it.</param>
/// <param name="Added">Lines the diff adds. Zero when the diff does not mention the file.</param>
/// <param name="Removed">Lines the diff removes.</param>
/// <param name="InTheDiff">
/// Whether the round's diff names this path, which is what makes it part of the change
/// that would ship. False for a file git status sees but the diff does not — an untracked
/// scratch file, or a file whose change the round never got as far as committing or staging.
/// </param>
/// <param name="Uncommitted">Whether <c>git status</c> still sees the file uncommitted.</param>
public sealed record ChangedFile(
    string Path,
    string? PreviousPath,
    FileChange Change,
    int Added,
    int Removed,
    bool InTheDiff,
    bool Uncommitted);

/// <summary>What git observed happen to a file.</summary>
public enum FileChange
{
    /// <summary>The file's content changed.</summary>
    Modified,

    /// <summary>The file is new.</summary>
    Added,

    /// <summary>The file is gone.</summary>
    Deleted,

    /// <summary>The file moved.</summary>
    Renamed,

    /// <summary>The file was copied from another.</summary>
    Copied,

    /// <summary>The file's kind changed — a file became a symlink, or the reverse.</summary>
    TypeChanged,

    /// <summary>Two changes to the same path could not be reconciled by git.</summary>
    Unmerged,

    /// <summary>git status saw the path and the repository has never heard of it.</summary>
    Untracked,
}

/// <summary>
/// One command the round ran, and what it returned. The exit code is the outcome and
/// nothing is inferred from it: the factory does not decide which of these were tests,
/// because the repository's own scripts decide that, and a factory that guessed would be
/// a factory imposing a test convention on the project it is building (ADR-0011).
/// </summary>
/// <param name="Sequence">
/// The number the wrapper assigned when the record was written. It increases down the
/// file even where one recorded command wraps another, so it is the record's own order.
/// </param>
/// <param name="Label">The label the round gave the invocation, or null when it gave none.</param>
/// <param name="Command">The command line, as the wrapper saw it.</param>
/// <param name="WorkingDirectory">Where it ran, when the wrapper recorded it.</param>
/// <param name="ExitCode">What it exited with. The whole of its outcome.</param>
/// <param name="DurationMs">How long it took, when the wrapper recorded it.</param>
/// <param name="StdoutTail">The end of what it wrote to stdout, when it was captured.</param>
/// <param name="StderrTail">The end of what it wrote to stderr, when it was captured.</param>
/// <param name="StdoutBytes">How much it wrote to stdout in total.</param>
/// <param name="StderrBytes">How much it wrote to stderr in total.</param>
public sealed record CommandOutcome(
    int Sequence,
    string? Label,
    string Command,
    string? WorkingDirectory,
    int ExitCode,
    long? DurationMs,
    string StdoutTail,
    string StderrTail,
    long StdoutBytes,
    long StderrBytes)
{
    /// <summary>
    /// Whether the command returned zero. The word "passed" is the factory's and the
    /// number is the command's: a build that ran and failed is a fact about the build,
    /// not a failure the factory can retry (ADR-0001).
    /// </summary>
    public bool Passed => ExitCode == 0;
}

/// <summary>One commit the round made, as <c>git log</c> reported it inside the container.</summary>
/// <param name="Id">The full commit id.</param>
/// <param name="Subject">The commit's first line.</param>
public sealed record Commit(string Id, string Subject);

/// <summary>
/// What the container observed about itself. Every value here was read inside the worker
/// container, and the credential names are names only — the image records the names so a
/// reviewer can see what the agent was handed and nothing secret travels with them.
/// </summary>
/// <param name="User">The account the round ran as. `agent`, and never root.</param>
/// <param name="Uid">That account's numeric id.</param>
/// <param name="IsARepository">Whether the round's working tree was a git repository at all.</param>
/// <param name="Branch">The branch the round ended on.</param>
/// <param name="StartHead">The commit the round started from, which is what its diff is against.</param>
/// <param name="Head">The commit the round ended on.</param>
/// <param name="Remotes">
/// The tree's remotes, verbatim, as <c>git remote -v</c> printed them. Recorded rather
/// than interpreted: what a reviewer reads it for is whether the round had anywhere to
/// push to, and whether the URL it pushed to carried a credential of its own.
/// </param>
/// <param name="CredentialNames">
/// The names of the credential-shaped environment variables the round was handed. The
/// image never records their values, so neither does the factory, and an empty list is
/// the evidence behind ADR-0006's claim rather than an assertion of it.
/// </param>
/// <param name="Git">The git the round built with.</param>
/// <param name="Agent">The OpenCode CLI the round drove.</param>
/// <param name="OperatingSystem">What the round ran on.</param>
/// <param name="RoundExitCode">
/// What the round's own last command returned, as the image recorded it, or null when the
/// result file carries no such number.
///
/// <para>
/// This is the one number in the result that is about the <em>round</em> rather than about
/// anything in it, and it is the boundary the design's own sentence does not draw. The exit
/// code of a <em>command the round ran</em> is a test failing or a lint complaining: data,
/// and a result a reviewer judges (ADR-0001). The exit code of the round's own last command
/// is whether the round <em>finished</em> — and for the script this factory runs, that last
/// command is the agent itself, because <c>WorkerRoundRunner.RoundScript</c> ends with the
/// <c>opencode run</c> invocation and nothing after it. A rate limit, a refused provider, a
/// missing model and a malformed brief all arrive as a non-zero here, having produced
/// nothing at all, and nothing else in the file distinguishes them from a round that
/// changed nothing on purpose.
/// </para>
///
/// <para>
/// The image's own README says the same thing from the other side: the container's exit
/// code is about the container, not the round, and "a round whose last command failed still
/// exits 0 with roundExitCode inside the file". That is why this is read out of the result
/// and not out of the daemon — <c>docker logs</c> exits zero whatever the container did, so
/// there is nothing on that side to read (#22).
/// </para>
/// </param>
public sealed record RoundEnvironment(
    string? User,
    int Uid,
    bool IsARepository,
    string? Branch,
    string? StartHead,
    string? Head,
    string Remotes,
    IReadOnlyList<string> CredentialNames,
    string? Git,
    string? Agent,
    string? OperatingSystem,
    int? RoundExitCode = null)
{
    /// <summary>
    /// Whether the round is known to have run to completion — its own last command returned
    /// zero, or the result file simply did not say and nothing is being claimed.
    /// </summary>
    /// <remarks>
    /// False means "this is not a finished round", never "this round failed", and it is
    /// deliberately true for a null: an image that did not record the number has not
    /// claimed the round failed either, and inventing a failure out of an absent field is
    /// the guessing the whole classification policy refuses to do.
    /// </remarks>
    public bool TheRoundRan => RoundExitCode is not { } code || code == 0;
}
