namespace AgentFactory.Rounds;

using AgentFactory.Containers;
using AgentFactory.Credentials;
using AgentFactory.Failures;
using AgentFactory.Observability;
using AgentFactory.Projects;
using AgentFactory.Results;
using Microsoft.Extensions.Logging;

// The host diff. The round's own tree is on the host because the host has to be able to
// reach the commit once the container is gone (ADR-0006), and `git` against that tree is
// how a reviewer gets the change itself rather than a bounded record of it.

/// <summary>
/// A round, run for real: one fresh worker container from the project's configured image,
/// the agent driven inside it, and the result, the tree and the log lifted back out. One
/// call is one round and one result, which is the whole of what the orchestrator is
/// promised (ADR-0004) and the only thing it knows.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam's implementation and the agent's driver. The round's brief reaches the
/// container as an environment variable and the container's entrypoint drives the
/// OpenCode CLI non-interactively with it (ADR-0004, ADR-0005). The reviewer's words are
/// never written into the command: a feedback of <c>"; rm -rf /; echo "</c> has to arrive
/// as those words rather than as shell syntax, and the brief is quoted into a file inside
/// the container and handed to the CLI as a file rather than as a command line.
/// </para>
/// <para>
/// What comes back is not authored here either. The container records what it observed —
/// git for the files changed, the recording wrapper for the commands run, exit codes for
/// the outcomes — and the <see cref="RoundResultDeriver"/> reads that into a result. The
/// only thing in a result a model wrote is the agent's note, and nothing is derived from
/// it (ADR-0011).
/// </para>
/// <para>
/// Credentials are the sharp line here. A worker container holds <strong>no</strong>
/// credential that can write to a remote, and the project file's GitHub key is never
/// read: this component never calls <see cref="ICredentialReader.Read"/> with that name,
/// so it cannot hand the value over even by accident (ADR-0006). The project's LLM
/// credential is a different matter and does go in, because the agent cannot reach a
/// provider without one; it is scoped to the one project, to the one container, and dies
/// with the container (story 66, 67). When the environment does not have it, the round
/// still runs and still records — the round is not refused for a missing key, because a
/// refusal would be the factory guessing whether the agent needs it, and a warning is
/// said out loud rather than left to be discovered on a board.
/// </para>
/// <para>
/// <strong>What this component logs and what it is given.</strong> It is handed a
/// <see cref="Round"/>, which carries the work item's id, project and issue — so it opens a
/// <see cref="WorkItemScope"/> with them, and every record the container runtime, the
/// result deriver and the Docker CLI write for the next ninety minutes is stamped with the
/// work item it was for. None of those three is handed any of it, which is the point: the
/// deriver's constructor is still one logger, and a record it writes an hour into a round
/// still says which work item it belongs to. The round <em>number</em> is not in the scope
/// this opens, because the round it was handed does not carry one; it rides on the scope
/// the loop opens around the call, so a record written deep in a round names both.
/// </para>
/// </remarks>
public sealed class WorkerRoundRunner : INOpenCode
{
    /// <summary>
    /// What a round runs in its container: write the brief out, read it back through the
    /// image's recording wrapper, and then hand it to the agent. It is a script rather
    /// than a command list because a round is several steps and each of them belongs in
    /// the round's own record.
    /// </summary>
    /// <remarks>
    /// The script is fixed: every line in it is the factory's, and the reviewer's words
    /// reach the container only as <c>"$AGENT_FACTORY_BRIEF"</c>. A round therefore has
    /// the same command line whatever issue it is building, which is what makes "the
    /// reviewer's words cannot become shell syntax" a property of the code rather than a
    /// claim about it.
    /// </remarks>
    public const string RoundScript = """
        set -uo pipefail
        out="${AGENT_FACTORY_OUT:-/out}"
        cd "$AGENT_FACTORY_WORK"
        printf '%s' "$AGENT_FACTORY_BRIEF" > "$out/brief.md"
        run -o 'the brief this round was given' cat "$out/brief.md"
        run -o 'the agent, building and testing the change' \
            opencode run --standalone --auto --file "$out/brief.md" -- "$AGENT_FACTORY_AGENT_PROMPT"
        """;

    /// <summary>
    /// The one message the factory sends the agent. It is fixed too, and it points at the
    /// brief rather than carrying it: the work item's own words are in the file, and a
    /// prompt that quoted them would be a prompt a shell could have rewritten.
    /// </summary>
    public const string AgentPrompt =
        "Read /out/brief.md. It is the work item you are building, in the words the maintainer and "
            + "the reviewer wrote them. Do that work in this repository, then write a short note to "
            + "/out/note.md saying what you changed and why.";

    private readonly ContainerRuntime _containers;
    private readonly ProjectLoadReport _projects;
    private readonly FactoryOptions _options;
    private readonly RoundResultDeriver _deriver;
    private readonly ICredentialReader _credentials;
    private readonly ILogger<WorkerRoundRunner> _logger;

    public WorkerRoundRunner(
        ContainerRuntime containers,
        ProjectLoadReport projects,
        FactoryOptions options,
        RoundResultDeriver deriver,
        ICredentialReader credentials,
        ILogger<WorkerRoundRunner> logger)
    {
        _containers = containers ?? throw new ArgumentNullException(nameof(containers));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _deriver = deriver ?? throw new ArgumentNullException(nameof(deriver));
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
        // Opened first, before anything can go wrong, so that even a round that cannot
        // start is attributable. A refusal with no work item on it is the one kind of record
        // that cannot be grouped with the rest of a work item's history, and this is the
        // cheapest possible place to make sure there isn't one.
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
            // Retrying it would spend two more containers to be told the same thing.
            _logger.LogWarning(
                "A round of {Project}#{Issue} cannot run: no project file is being served for {Project}, "
                    + "so there is no worker image to start from. Nothing about a second attempt would be different.",
                round.Project,
                round.IssueNumber,
                round.Project);

            return RoundResult.Failed(FailureClass.Permanent);
        }

        var landing = Path.Combine(_options.RoundsDirectory, round.WorkItemId.ToString("N"));

        // The name a round's container is created under, and the only handle teardown
        // needs. It carries the work item so a container can be found on a busy daemon,
        // and so two rounds for the same work item cannot both be running: the second
        // create is refused by name rather than silently doubling up.
        var name = $"agent-factory-round-{round.WorkItemId:N}";

        var request = new WorkerContainerRequest(
            name,
            project.WorkerImage,
            ["shell", "-c", RoundScript],
            EnvironmentFor(round, project));

        WorkerContainerRun run;
        try
        {
            run = await _containers.RunAsync(
                request,
                landing,
                new RoundLog(_logger),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The loop stopped waiting for this round. It has already recorded it as timed
            // out, and the container runtime has already removed the container: this is
            // the call ending, not a failure to report. Rethrown because a cancelled
            // operation says so, and because the loop asked for the round to stop.
            _logger.LogInformation(
                "A round of {Project}#{Issue} was ended by the factory, and its container removed with it.",
                round.Project,
                round.IssueNumber);

            throw;
        }
        catch (FactoryFailure broken)
        {
            // A failure the round could not survive rather than one the round produced.
            // Which kind it is was decided where it was observed — the container runtime
            // knows it was a daemon that was not answering, and this runner knows a missing
            // project file is not going to appear — and it is carried here rather than
            // worked out again from a message, because a message is not a classification.
            // The loop reads it and decides whether to ask for the round again.
            _logger.LogWarning(
                broken,
                "A round of {Project}#{Issue} had no container to run in: {Reason}. "
                    + "The factory has read that as a {Classification} failure.",
                round.Project,
                round.IssueNumber,
                broken.Message,
                broken.Class);

            return RoundResult.Failed(broken.Class);
        }

        _logger.LogInformation(
            "A round of {Project}#{Issue} finished; its result, its tree and its log are in {Landing}.",
            round.Project,
            round.IssueNumber,
            landing);

        // The deriver never returns without a result, so this is total: a missing, a
        // truncated and an unreadable result file all come back as a DerivedResult
        // carrying why it is not a record. Deriving before deciding what to do about the
        // round is what lets the diff below be generated for all three of those shapes,
        // rather than only for the one where the result file happened to parse — and the
        // result file's own start commit is the best base for that diff.
        var derived = _deriver.Derive(run.ResultFile, run.LogTail);

        // The diff, generated here on the host and out of the tree that just came out of
        // the container (ADR-0006). It is the host's own git against a directory on the
        // host, so it is available with no network and no API, and it is generated
        // whether or not there is a payload to put it beside: a round that broke before
        // it could write a result file still left a tree, and what it managed to change
        // is the one thing a reviewer most wants to see about it.
        //
        // The base is the commit the round started from, as the round's own record
        // states it. Where there is no such record — a round whose result could not be
        // read at all — the branch the round was given is used instead, which is right
        // for a fresh clone: its history contains that branch as an ancestor whatever the
        // round did on top of it.
        var diff = await HostDiffReader.OfAsync(
            run.RoundTree,
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

        if (run.ResultFile is null)
        {
            // A round with no result is not a round with nothing: its log is the whole of
            // what there is to show, so it travels on the result rather than being lost in
            // a log line, and the diff travels with it because the tree is on the host
            // whatever the container failed to write down. It is also permanent, and
            // deliberately so — the container ran, and whatever stopped it from writing
            // its result would stop a second container the same way. Note what this is
            // *not*: the container failing to start, which is the transient shape of the
            // same-looking failure and is classified above.
            _logger.LogWarning(
                "A round of {Project}#{Issue} came back without a result: it ran and produced nothing, "
                    + "so another attempt would only be told the same thing. Its log ends: {Log}",
                round.Project,
                round.IssueNumber,
                run.LogTail);

            return RoundResult.Failed(FailureClass.Permanent, run.LogTail, diff);
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
                + "from what its container observed, and the host generated a diff of {DiffFiles} file(s) from the "
                    + "tree it left.{Reading}",
            round.Project,
            round.IssueNumber,
            derived.FilesChanged.Count,
            derived.CommandsRun.Count,
            DiffDocument.Parse(diff.Text).Files.Count,
            derived.UnreadableBecause is { } unreadable ? " It could not be read: " + unreadable : string.Empty);

        // Produced whatever the round's own commands returned, and whatever the result
        // file's shape was. A round that ran and whose change failed its own tests is
        // Produced, with the failing exit codes in the payload — that is the structural
        // half of "a build that fails its tests is not retried" (ADR-0001), and it is why
        // nothing here reads the deriver's outcome to decide what to return.
        return RoundResult.Produced(
            ResultPayload.Of(derived, run.LogTail),
            derived.AgentNote,
            run.LogTail,
            diff);
    }

    /// <summary>
    /// The round's environment, and the one place a credential value is put into a worker
    /// container.
    /// </summary>
    /// <remarks>
    /// The project's <c>keys.github</c> name is not read here, anywhere, ever. That is
    /// what makes ADR-0006 structural rather than a matter of care: the value is never in
    /// this component's hands, so a round cannot be given one by accident or by a later
    /// edit that adds it. The LLM key is read because the agent cannot reach a provider
    /// without it, and it is put in under the name the project file gave — a project
    /// declares a name, and the name is the same on both sides of the container boundary.
    /// </remarks>
    private Dictionary<string, string> EnvironmentFor(Round round, Project project)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // The tree arrives over the network because a worker container is given no
            // host path to put one in (ADR-0010).
            ["AGENT_FACTORY_REPO_URL"] = round.RepoUrl,
            ["AGENT_FACTORY_BASE_REF"] = round.BaseBranch,
            ["AGENT_FACTORY_BRIEF"] = RoundBrief.For(round),
            ["AGENT_FACTORY_AGENT_PROMPT"] = AgentPrompt,
        };

        if (_credentials.Read(project.LlmKeyName) is { Length: > 0 } key)
        {
            environment[project.LlmKeyName] = key;

            _logger.LogInformation(
                "A round of {Project} is being handed {Key} for the life of its container. The name, never the value.",
                project.Name,
                project.LlmKeyName);
        }
        else
        {
            // Said out loud rather than left to be discovered. The round still runs: a
            // factory that refused here would be deciding for itself that the agent
            // cannot work without a key, and the agent is the thing that knows. What the
            // round does about it is in the round's own log, and the result records
            // which credentials it was actually handed.
            _logger.LogWarning(
                "A round of {Project} is being handed no LLM credential: the project names {Key} and this "
                    + "process's environment does not have it. The round runs anyway and whatever the agent says "
                    + "about that is in the round's own log.",
                project.Name,
                project.LlmKeyName);
        }

        return environment;
    }

    /// <summary>
    /// A round's log, line by line, as the round writes it. This is the live channel out
    /// of a round (ADR-0010) and the only place the factory's own logs say anything about
    /// what a round was doing while it was doing it. The end of it also travels on the
    /// round's result, so a round that produced nothing usable is still readable by a
    /// reviewer (story 29).
    /// </summary>
    /// <remarks>
    /// Debug, and there is a great deal of it — every line a build prints, for ninety
    /// minutes, across two containers. At Information this would be unreadable and
    /// undiagnosable; at Debug it is what an operator turns on when they want to watch a
    /// round, and it is where the live progress channel was always meant to land. The line
    /// is the container's own and is bounded by nothing here, which is the other reason it
    /// cannot be higher than Debug: an unbounded line at a level every deployment enables
    /// is a log a deployment cannot keep.
    /// </remarks>
    private sealed class RoundLog(ILogger logger) : IProgress<string>
    {
        public void Report(string value) =>
            logger.LogDebug("The round says: {Line}", value);
    }
}
