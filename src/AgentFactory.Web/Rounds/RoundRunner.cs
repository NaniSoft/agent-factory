namespace AgentFactory.Rounds;

using AgentFactory.Application.Credentials;
using AgentFactory.Application.Observability;
using AgentFactory.Application.Projects;
using AgentFactory.Application;
using AgentFactory.Domain.Failures;
using AgentFactory.Domain.Projects;
using AgentFactory.Infrastructure.Projects;
using AgentFactory.Results;
using Microsoft.Extensions.Logging;

// The host diff. The round's tree is on the host because the host has to be able to reach
// the commit once the round is over (ADR-0006), and `git` against that tree is how a
// reviewer gets the change itself rather than a bounded record of it.

/// <summary>
/// A round, run for real: one fresh working tree, the agent driven in it as this machine's
/// own child process, and the result, the tree and the log gathered where the factory keeps
/// its rounds. One call is one round and one result, which is the whole of what the
/// orchestrator is promised (ADR-0004) and the only thing it knows.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam's implementation and the agent's driver. The brief reaches the agent as
/// a file in the round's landing directory, and the process this component starts <em>is</em>
/// the OpenCode CLI, run non-interactively (ADR-0004, ADR-0005). There is no script between
/// the factory and the agent any more, and that is the round boundary made structural: the
/// exit code this component reads is the agent's own, because nothing wraps it. Where the
/// round script once had to end with <c>opencode run</c> and nothing after it, the child
/// process now ends where the agent ends, by construction.
/// </para>
/// <para>
/// The reviewer's words are never written into the command line: a feedback of
/// <c>"; rm -rf /; echo "</c> has to arrive as those words rather than as shell syntax, and
/// the brief is quoted into a file and handed to the CLI as <c>--file</c> rather than as an
/// argument. The command line is the same whatever issue a round is building.
/// </para>
/// <para>
/// What comes back is not authored here either. The round's collector gathers what the
/// round observed — git for the files changed, the recording shim for the commands run,
/// the process exit code for the round's own ending — and the
/// <see cref="RoundResultDeriver"/> reads that into a result. The shim under-reports, as
/// its own file states: a command the agent reaches without the <c>run</c> prefix is not
/// recorded, and ADR-0011's guarantee is carried by <c>git status</c> and <c>git diff</c>,
/// not by the command log.
/// </para>
/// <para>
/// Credentials are the sharp line here. A round holds <strong>no</strong> credential that
/// can write to a remote, and the project file's GitHub key is never read: this component
/// never calls <see cref="ICredentialReader.Read"/> with that name, so it cannot hand the
/// value over even by accident (ADR-0006). The project's LLM credential is a different
/// matter and does go in, because the agent cannot reach a provider without one; it is
/// scoped to the one project, lives for one child process, and dies with it. When the
/// environment does not have it, the round still runs and still records — a refusal here
/// would be the factory guessing whether the agent needs one, and a warning is said out
/// loud rather than left to be discovered on a board.
/// </para>
/// <para>
/// A known gap, stated rather than hidden: a round is a process tree, not a container. A
/// round cancelled by this factory kills its whole tree; a round whose <em>factory</em>
/// died without the chance to cancel anything may leave the agent running with nowhere to
/// report. Reconciliation re-asks the round either way — the store's pending-round row is
/// the whole of what a restart needs — but the orphan's partial work has no name on it to
/// find it by.
/// </para>
/// <para>
/// <strong>What this component logs and what it is given.</strong> It is handed a
/// <see cref="Round"/>, which carries the work item's id, project and issue — so it opens a
/// <see cref="WorkItemScope"/> with them, and every record the process runner, the collector
/// and the result deriver write for the next ninety minutes is stamped with the work item it
/// was for. The round <em>number</em> is not in the scope this opens, because the round it
/// was handed does not carry one; it rides on the scope the loop opens around the call, so a
/// record written deep in a round names both.
/// </para>
/// </remarks>
public sealed class RoundRunner : INOpenCode
{
    /// <summary>The folder inside a round's landing directory the round works in.</summary>
    public const string TreeFolder = "tree";

    /// <summary>The folder inside a routing round's landing directory, which is empty on purpose.</summary>
    public const string WorkFolder = "work";

    /// <summary>
    /// The one message the factory sends the agent. It is fixed apart from the landing
    /// directory it names, and it points at the brief rather than carrying it: the work
    /// item's own words are in the file, and a prompt that quoted them would be a prompt a
    /// shell could have rewritten.
    /// </summary>
    public static string AgentPrompt(string outDirectory) =>
        "Read " + BriefPath(outDirectory) + ". It is the work item you are building, in the words the maintainer and "
            + "the reviewer wrote them. Do that work in this repository, then write a short note to "
            + Path.Combine(outDirectory, RoundCollector.NoteFileName) + " saying what you changed and why.";

    /// <summary>
    /// The message a routing round is given. It names one output file and does not mention a
    /// repository, a test or a commit, because the round is given none of those — a prompt
    /// that mentioned them would be asking for work this round cannot do, and the most
    /// expensive thing an agent can be told is to go and do it anyway.
    /// </summary>
    public static string RoutingAgentPrompt(string outDirectory) =>
        "Read " + BriefPath(outDirectory) + ". It is one issue from this repository, in the maintainer's own words, "
            + "and it is all you are given. Decide which of the three verdicts the brief names fits it, "
            + "then write that decision to " + Path.Combine(outDirectory, RoutingFileName)
            + " in exactly the shape the brief gives. "
            + "Write nothing else and change nothing; there is no repository here.";

    /// <summary>The file the round's verdict is written to, beside its result.</summary>
    public const string RoutingFileName = "routing.json";

    /// <summary>The prompt a round of this purpose is driven with, and why the choice is here and not at the call site.</summary>
    public static string PromptFor(RoundPurpose purpose, string outDirectory) =>
        purpose == RoundPurpose.Route ? RoutingAgentPrompt(outDirectory) : AgentPrompt(outDirectory);

    private static string BriefPath(string outDirectory) =>
        Path.Combine(outDirectory, RoundCollector.BriefFileName);

    private readonly IProcessCli _processes;
    private readonly ProjectLoadReport _projects;
    private readonly FactoryOptions _options;
    private readonly RoundResultDeriver _deriver;
    private readonly Routing.RoutingReader _routing;
    private readonly ICredentialReader _credentials;
    private readonly ILogger<RoundRunner> _logger;

    public RoundRunner(
        IProcessCli processes,
        ProjectLoadReport projects,
        FactoryOptions options,
        RoundResultDeriver deriver,
        ICredentialReader credentials,
        ILogger<RoundRunner> logger)
        : this(processes, projects, options, deriver, new Routing.RoutingReader(), credentials, logger)
    {
    }

    /// <summary>
    /// The composition the host builds. It takes the reader rather than making one so that
    /// this component is testable against a real file on disk without an agent, which is how
    /// the routing layer is proved hermetically — and taking it as a dependency is also what
    /// keeps "one reader of that file's shape" true by construction rather than by care.
    /// </summary>
    public RoundRunner(
        IProcessCli processes,
        ProjectLoadReport projects,
        FactoryOptions options,
        RoundResultDeriver deriver,
        Routing.RoutingReader routing,
        ICredentialReader credentials,
        ILogger<RoundRunner> logger)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _deriver = deriver ?? throw new ArgumentNullException(nameof(deriver));
        _routing = routing ?? throw new ArgumentNullException(nameof(routing));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<RoundResult> RunRoundAsync(Round round, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(round);

        // Opened first, before anything can go wrong, so that even a round that cannot
        // start is attributable. A refusal with no work item on it is the one kind of record
        // that cannot be grouped with the rest of a work item's history, and this is the
        // cheapest possible place to make sure there isn't one.
        using var trace = _logger.ForWorkItem(round.WorkItemId, round.Project, round.IssueNumber);

        var project = _projects.Projects.FirstOrDefault(candidate => candidate.Name == round.Project);
        if (project is null)
        {
            // The project file this work item was taken from is not being served any more.
            // That is a round that cannot start, not a round that failed, and it is
            // permanent rather than transient: configuration loads at start and does not
            // hot reload, so nothing about the second attempt would differ from the first.
            // Retrying it would spend two more rounds to be told the same thing.
            _logger.LogWarning(
                "A round of {Project}#{Issue} cannot run: no project file is being served for {Project}. "
                    + "Nothing about a second attempt would be different.",
                round.Project,
                round.IssueNumber,
                round.Project);

            return RoundResult.Failed(FailureClass.Permanent);
        }

        // **One landing directory per round attempt, and the round number is in it.**
        // A single directory per work item meant round 2's tree landed inside round 1's, so
        // the board's diff for round 2 was round 1's diff and the merger — which reads the
        // same path — would have pushed round 1's commit on an approve of round 2 (#22).
        //
        // The *attempt* is in the path as well as the round, and that is the second half of
        // the same hazard: a round that failed transiently is asked for again as the same
        // round, so a per-round directory alone would put the retry's tree inside the first
        // attempt's. Nothing in the design wants the two kept apart by anything but the
        // path, and this is the place that knows both numbers.
        var landing = LandingFor(_options, round);
        Directory.CreateDirectory(landing);

        string workDirectory;
        try
        {
            workDirectory = await WorkspaceFor(round, landing, cancellationToken).ConfigureAwait(false);
        }
        catch (PermanentFailure broken)
        {
            // The tree the round was to work in could not be fetched. The classification
            // was decided where the refusal was observed — the clone knows a round holds
            // no credential that can fetch a private repository — and it is carried here
            // as a result rather than thrown, because one call is one round and one
            // result, and a refusal to begin is a round that ended (ADR-0004).
            _logger.LogWarning(
                broken,
                "A round of {Project}#{Issue} could not begin: {Reason}. The factory has read that as a permanent failure.",
                round.Project,
                round.IssueNumber,
                broken.Message);

            return RoundResult.Failed(FailureClass.Permanent, broken.Message);
        }

        // The base commit, captured before the agent touches anything. This is the commit
        // the round's diff is measured from, and it is why a committed change and an
        // uncommitted one both appear in it.
        var startHead = round.Purpose == RoundPurpose.Build
            ? await GitAsync(workDirectory, ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false)
            : null;

        RoundShim.WriteInto(landing);
        var briefPath = BriefPath(landing);
        await File.WriteAllTextAsync(
            briefPath,
            RoundBrief.For(round),
            cancellationToken).ConfigureAwait(false);

        // The round's environment, layered over this process's own for the one child. The
        // landing directory is at the front of the PATH, so the `run` the brief asks for is
        // the round's own recording shim and not whatever else the machine holds.
        var environment = EnvironmentFor(round, project, landing, workDirectory);

        _logger.LogInformation(
            "A round of {Project}#{Issue} starts {Purpose} work in {Work}; the agent is this machine's own child process.",
            round.Project,
            round.IssueNumber,
            round.Purpose,
            workDirectory);

        int roundExitCode;
        string log;
        try
        {
            var agent = await _processes.InvokeAsync(
                new ProcessRequest(
                    "opencode",
                    [
                        "run",
                        "--standalone",
                        "--auto",
                        "-m",
                        project.LlmModel,
                        "--file",
                        briefPath,
                        "--",
                        PromptFor(round.Purpose, landing),
                    ],
                    WorkingDirectory: workDirectory,
                    Environment: environment),
                new RoundLog(_logger),
                cancellationToken).ConfigureAwait(false);

            roundExitCode = agent.ExitCode;
            log = agent.Tail;
        }
        catch (OperationCanceledException)
        {
            // The loop stopped waiting for this round. It has already recorded it as timed
            // out, and the process tree has already been killed: this is the call ending,
            // not a failure to report. Rethrown because a cancelled operation says so, and
            // because the loop asked for the round to stop.
            _logger.LogInformation(
                "A round of {Project}#{Issue} was ended by the factory, and its process tree killed with it.",
                round.Project,
                round.IssueNumber);

            throw;
        }
        catch (PermanentFailure broken)
        {
            // There is no agent on this machine to run the round with. The binary is either
            // there or it is not, and a second attempt in ten seconds begins a second
            // identical failure — the classification was decided where the absence was
            // observed, and it is carried here rather than worked out again from a message.
            _logger.LogWarning(
                broken,
                "A round of {Project}#{Issue} has no agent to run in: {Reason}. The factory has read that as a permanent failure.",
                round.Project,
                round.IssueNumber,
                broken.Message);

            return RoundResult.Failed(FailureClass.Permanent, broken.Message);
        }

        _logger.LogInformation(
            "A round of {Project}#{Issue} finished with exit code {ExitCode}; its result, its tree and its log are in {Landing}.",
            round.Project,
            round.IssueNumber,
            roundExitCode,
            landing);

        // The collector writes the same result file the image's collector wrote — the same
        // schema, the same line order — because the deriver on the other side is the one
        // component whose contract this file exists to keep. It runs whatever the round did:
        // an agent that exited non-zero still gets its observations written, and the exit
        // code is *in* the file rather than in the collector's own status.
        var resultFile = RoundCollector.Collect(
            landing,
            workDirectory,
            startHead,
            roundExitCode,
            CredentialNames(environment));

        if (resultFile is null)
        {
            _logger.LogWarning(
                "A round of {Project}#{Issue} left no result the factory could write. Its log ends: {Log}",
                round.Project,
                round.IssueNumber,
                log);
        }

        // The deriver never returns without a result, so this is total: a missing, a
        // truncated and an unreadable result file all come back as a DerivedResult
        // carrying why it is not a record. Deriving before deciding what to do about the
        // round is what lets the diff below be generated for all three of those shapes,
        // rather than only for the one where the result file happened to parse — and the
        // result file's own start commit is the best base for that diff.
        var derived = _deriver.Derive(resultFile, log);

        // The routing verdict, read here rather than in the loop, for two reasons. The loop
        // never learns where on disk a round's files land — the round runner is the one that
        // knows — and a round's verdict is known to be unreadable *before* the loop is asked
        // to move a work item on it, which is what stops "the round said nothing" and "the
        // round said Build" from being the same event on the board.
        //
        // Read for every round rather than only for routing ones. A build round writes no
        // verdict, and the cost of asking is one read that returns "no verdict was written"
        // — a true statement about a build round, and a different one from the silent
        // absence a conditional read would leave behind.
        var routing = _routing.Read(Path.Combine(landing, RoutingFileName));

        // The diff, generated on the host out of the tree the round just left (ADR-0006).
        // It is the host's own git against a directory on the host, so it is available with
        // no network and no API, and it is generated whether or not there is a payload to
        // put it beside: a round that broke before it could write a result file still left
        // a tree, and what it managed to change is the one thing a reviewer most wants to
        // see about it.
        //
        // The base is the commit the round started from, as the round's own record states
        // it. Where there is no such record — a round whose result could not be read at all
        // — the branch the round was given is used instead, which is right for a fresh
        // clone: its history contains that branch as an ancestor whatever the round did on
        // top of it.
        var tree = round.Purpose == RoundPurpose.Build ? Path.Combine(landing, TreeFolder) : null;
        var diff = await HostDiffReader.OfAsync(
            tree,
            derived.Environment.StartHead,
            round.BaseBranch,
            derived.DiffTruncated,
            cancellationToken).ConfigureAwait(false);

        if (diff.UnavailableBecause is { } noDiff)
        {
            _logger.LogWarning(
                "A round of {Project}#{Issue} leaves no diff on the board: {Reason}",
                round.Project,
                round.IssueNumber,
                noDiff);
        }

        if (resultFile is null)
        {
            // A round with no result is not a round with nothing: its log is the whole of
            // what there is to show, so it travels on the result rather than being lost in
            // a log line, and the diff travels with it because the tree is on the host
            // whatever the round failed to write down. It is also permanent, and
            // deliberately so — the round ran, and whatever stopped its result from being
            // written would stop a second attempt the same way.
            _logger.LogWarning(
                "A round of {Project}#{Issue} came back without a result: it ran and produced nothing, "
                    + "so another attempt would only be told the same thing. Its log ends: {Log}",
                round.Project,
                round.IssueNumber,
                log);

            return RoundResult.Failed(FailureClass.Permanent, log, diff);
        }

        // The counts are structured fields rather than words in a sentence, and this is the
        // record that answers "what did this round produce" for a work item whose reviewer
        // wants the shape of the change rather than its prose: how many files, how many
        // commands, how big the host's own diff is. The result file's text is on the
        // record in the store and the log tail is bounded, and neither belongs in a log
        // line — a ninety-minute build's output does not go in a log record any more than it
        // goes in a database row.
        _logger.LogInformation(
            "A round of {Project}#{Issue} derived {Files} changed file(s) and {Commands} recorded command(s) "
                + "from what the round observed, and the host generated a diff of {DiffFiles} file(s) from the "
                    + "tree it left.{Reading}",
            round.Project,
            round.IssueNumber,
            derived.FilesChanged.Count,
            derived.CommandsRun.Count,
            DiffDocument.Parse(diff.Text).Files.Count,
            derived.UnreadableBecause is { } unreadable ? " It could not be read: " + unreadable : string.Empty);

        // **The round's own exit code, and the whole of the outcome decision.** This is the
        // judgement the design's sentence did not make, and it is made here rather than in
        // the loop because this is the component that read the number.
        //
        // The process this factory started *is* the agent, so a non-zero exit code means the
        // round did not run to completion — a rate limit, a refused provider, a missing
        // model, a brief the agent would not accept — and in every one of those cases the
        // round produced nothing a reviewer can judge. Reporting that as `Produced` with an
        // empty diff is what made the first real run's cards lie: `data-outcome="Produced"
        // data-diff-state="empty"` and the sentence "Empty. Nothing on disk differs from
        // the commit the round started at", which is a confident false claim about a round
        // that never started (#22).
        //
        // The commands *inside* the round are a different matter and are not read here at
        // all: a build whose tests failed returns zero from the agent, so it is still
        // `Produced` and the failing exit code stays data in the payload, which is what
        // ADR-0001 asks for. The two cases never meet, because they are two different
        // numbers read in two different places — a single "did anything fail" flag would
        // put them back together and make a failing test retryable.
        if (!derived.Environment.TheRoundRan)
        {
            var exit = derived.Environment.RoundExitCode;

            // Transient, and this is the case it exists for. A rate limit is the most
            // obviously transient failure in this system, and the retry policy was built
            // without being able to see it. The costs are bounded and named: three attempts
            // at one round, holding its slot, then the round ends and the work item parks
            // where a human can finish it — so a genuinely permanent cause spends two extra
            // rounds rather than a reviewer's attention, while the other answer (permanent)
            // would park a work item a second attempt would have fixed. Nothing about the
            // round's *work* is retried, because there is no work: the payload and the diff
            // travel on the result either way.
            _logger.LogWarning(
                "A round of {Project}#{Issue} wrote a result but its own command exited {ExitCode}, so the round "
                    + "did not run to completion. Its tree is left where it stopped and nothing in it is a finished "
                    + "result. The commands it did run, and what they said, are in the payload. This is transient, "
                    + "so the round is asked for again as the same round rather than spent against the ceiling.",
                round.Project,
                round.IssueNumber,
                exit);

            return RoundResult.Failed(
                FailureClass.Transient,
                log,
                diff,
                ResultPayload.Of(derived, log),
                routing);
        }

        // Produced. The agent returned zero, so the round finished, and whatever the
        // commands inside it returned is a result a reviewer judges rather than a failure
        // the factory retries (ADR-0001).
        return RoundResult.Produced(
            ResultPayload.Of(derived, log),
            derived.AgentNote,
            log,
            diff,
            routing);
    }

    /// <summary>
    /// Where one round attempt's files land: the work item's own directory, a directory per
    /// round inside it, and a directory per attempt inside that.
    /// </summary>
    /// <remarks>
    /// The round number is what stops one round landing inside another's and the attempt
    /// number is what stops a retry landing inside the attempt it is retrying; the argument
    /// for both is on <see cref="RunRoundAsync"/>, where the directory is built.
    /// </remarks>
    public static string LandingFor(FactoryOptions options, Round round) => Path.Combine(
        options.RoundsDirectory,
        round.WorkItemId.ToString("N"),
        $"round-{round.RoundNumber}",
        $"attempt-{round.Attempt}");

    /// <summary>
    /// The working directory a round is given, prepared before anything else happens.
    /// </summary>
    /// <remarks>
    /// A build round fetches the repository over the network into its own landing
    /// directory — the factory has no shared tree to hand a round, and a shared tree is
    /// the thing two concurrent rounds would corrupt. The clone is unauthenticated and
    /// <c>--single-branch</c>, because the credentials the round holds are for the model,
    /// not for this repository (ADR-0006): a private repository is simply not something a
    /// round can fetch, and that is said in the refusal rather than discovered in git's
    /// error. <c>core.autocrlf=false</c> is pinned on the clone, because a tree checked
    /// out with the host's line-ending setting is not the tree the repository describes,
    /// and the diff a reviewer reads would be made of the host's bytes rather than the
    /// agent's (#26).
    ///
    /// <para>
    /// A routing round is given an empty directory and nothing else. The absence of the
    /// clone is the whole mechanism — the round cannot read a repository it was not
    /// given — and it is the same trick the container budget relied on: the round's costs
    /// are bounded by what the factory chooses to hand over.
    /// </para>
    /// </remarks>
    private async Task<string> WorkspaceFor(Round round, string landing, CancellationToken cancellationToken)
    {
        if (!RoundPurposes.FetchesTheRepository(round.Purpose))
        {
            var empty = Path.Combine(landing, WorkFolder);
            Directory.CreateDirectory(empty);
            return empty;
        }

        var tree = Path.Combine(landing, TreeFolder);
        var answer = await _processes.InvokeAsync(
            new ProcessRequest(
                "git",
                [
                    "-c", "core.quotepath=false",
                    "-c", "core.autocrlf=false",
                    "clone",
                    "--branch", round.BaseBranch,
                    "--single-branch",
                    round.RepoUrl,
                    tree,
                ],
                WorkingDirectory: landing),
            new RoundLog(_logger),
            cancellationToken).ConfigureAwait(false);

        if (answer.ExitCode != 0)
        {
            // Permanent, and this is the shape the image's entrypoint left: a round holds
            // no credential that can fetch a private repository, and a refusal to clone is
            // not a round that half-ran. The whole output is in the message, because this
            // log is the only account the round will leave.
            throw new PermanentFailure(
                $"the round's repository {round.RepoUrl} at {round.BaseBranch} could not be cloned: {answer.Tail}");
        }

        return tree;
    }

    /// <summary>
    /// The round's environment, and the one place a credential value is put into a round.
    /// </summary>
    /// <remarks>
    /// The project's <c>keys.github</c> name is not read here, anywhere, ever. That is
    /// what makes ADR-0006 structural rather than a matter of care: the value is never in
    /// this component's hands, so a round cannot be given one by accident or by a later
    /// edit that adds it. The LLM key is read because the agent cannot reach a provider
    /// without it — read by the project's own name for it, the reader's contract — and
    /// then put in under the *canonical* name the provider's tooling actually reads,
    /// derived from the model reference. The project's name for the key stops at the
    /// reader; the agent sees the canonical one, because the model-reference research
    /// (#38) measured that opencode looks for nothing else.
    /// <para>
    /// The round also configures the agent itself and leaves the tree it checks out
    /// nothing to say: <c>OPENCODE_DISABLE_PROJECT_CONFIG=1</c>, because a tree-controlled
    /// <c>opencode.json</c> can set the provider endpoint, and an endpoint the tree chose
    /// is the round's credential leaving for wherever the tree pointed it. The factory
    /// assumes its workloads are untrusted — they are written by agents.
    /// </para>
    /// </remarks>
    private Dictionary<string, string> EnvironmentFor(Round round, Project project, string landing, string workDirectory)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AGENT_FACTORY_BASE_REF"] = round.BaseBranch,
            ["AGENT_FACTORY_AGENT_PROMPT"] = PromptFor(round.Purpose, landing),
            // The model is the project's to state (#38), and it travels as a variable so
            // the reference is never shell syntax.
            ["AGENT_FACTORY_MODEL"] = project.LlmModel,
            // A tree that could configure the agent could point its credential anywhere.
            ["OPENCODE_DISABLE_PROJECT_CONFIG"] = "1",
            // Where the round's own files go: the brief the agent reads, the note and the
            // verdict it writes, the command log the shim appends to — all outside the
            // tree, so none of them appear in the change a reviewer is judging.
            ["AGENT_FACTORY_OUT"] = landing,
            ["AGENT_FACTORY_WORK"] = workDirectory,
            ["AGENT_FACTORY_COMMAND_LOG"] = Path.Combine(landing, RoundCollector.CommandLogFileName),
            // The recording shim, first in line: `run` in the round is this round's shim.
            ["PATH"] = landing + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
        };

        var canonical = LlmProviders.CanonicalEnvironmentVariableFor(project.LlmModel);

        if (_credentials.Read(project.LlmKeyName) is { Length: > 0 } key && canonical is { } name)
        {
            environment[name] = key;

            _logger.LogInformation(
                "A round of {Project} is being handed {Key} for the life of its round, under the canonical name {Name}. "
                    + "The names, never the value.",
                project.Name,
                project.LlmKeyName,
                name);
        }
        else
        {
            // Said out loud rather than left to be discovered. The round still runs: a
            // factory that refused here would be deciding for itself that the agent
            // cannot work without a key, and the agent is the thing that knows. What the
            // round does about it is in the round's own log, and the result records
            // which credentials it was actually handed. (The canonical name is never
            // null here — the loader refuses a model reference the table does not know —
            // so the else is the missing credential and only that.)
            _logger.LogWarning(
                "A round of {Project} is being handed no LLM credential: the project names {Key} and this "
                    + "process has no value for it. The round runs anyway and whatever the agent says "
                    + "about that is in the round's own log.",
                project.Name,
                project.LlmKeyName);
        }

        return environment;
    }

    /// <summary>
    /// The names of the credential-shaped variables the round was handed — names, never
    /// values — as the result file's header records them. The same shapes the image's
    /// collector looked for, so a round's record says the same thing about itself in either
    /// world.
    /// </summary>
    private static IReadOnlyList<string> CredentialNames(IReadOnlyDictionary<string, string> environment) =>
        environment.Keys
            .Where(name => System.Text.RegularExpressions.Regex.IsMatch(
                name,
                "(^|_)(API_?KEY|TOKEN|SECRET|PASSWORD|CREDENTIAL|AUTH|PRIVATE_?KEY)($|_)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>One small git question asked of the tree, answered or not. A failure is an absence, not a crash.</summary>
    private async Task<string?> GitAsync(string workDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await _processes.InvokeAsync(
                new ProcessRequest("git", arguments, WorkingDirectory: workDirectory),
                output: null,
                cancellationToken).ConfigureAwait(false);

            return answer.ExitCode == 0 ? answer.Tail.Trim() : null;
        }
        catch (PermanentFailure)
        {
            return null;
        }
    }

    /// <summary>
    /// A round's log, line by line, as the round writes it. This is the live channel out
    /// of a round and the only place the factory's own logs say anything about what a
    /// round was doing while it was doing it. The end of it also travels on the round's
    /// result, so a round that produced nothing usable is still readable by a reviewer.
    /// </summary>
    /// <remarks>
    /// Debug, and there is a great deal of it — every line a build prints, for ninety
    /// minutes. At Information this would be unreadable and undiagnosable; at Debug it is
    /// what an operator turns on when they want to watch a round, and it is where the live
    /// progress channel was always meant to land. The line is the agent's own and is
    /// bounded by nothing here, which is the other reason it cannot be higher than Debug:
    /// an unbounded line at a level every deployment enables is a log a deployment cannot
    /// keep.
    /// </remarks>
    private sealed class RoundLog(ILogger logger) : IProgress<string>
    {
        public void Report(string value) =>
            logger.LogDebug("The round says: {Line}", value);
    }
}
