namespace AgentFactory.Rounds;

using AgentFactory.Containers;
using AgentFactory.Failures;
using AgentFactory.Projects;
using Microsoft.Extensions.Logging;

/// <summary>
/// A round, run for real: one fresh worker container from the project's configured image,
/// one command inside it, and the result and the tree lifted back out. One call is one
/// round and one result, which is the whole of what the orchestrator is promised
/// (ADR-0004) and the only thing it knows.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam's implementation, not the agent. What runs inside the container today
/// is a plain command — the round's brief written down outside the working tree, and the
/// tree it was given read back through the image's own recording wrapper — because driving
/// the OpenCode CLI against a provider is the next ticket's (#9) and not this one's. The
/// payload it produces is correspondingly thin: the result file's own header line, read
/// verbatim, rather than a derived reading of the round (ADR-0011).
/// </para>
/// <para>
/// The brief reaches the container as an environment variable and is never written into
/// the command. A reviewer's words are not the factory's to quote, and a feedback of
/// <c>"; rm -rf /; echo "</c> has to arrive as those words rather than as shell syntax.
/// </para>
/// <para>
/// No credential is set here, of either kind. The container holds nothing that can write
/// to a remote (ADR-0006), and the LLM credential belongs to the agent that does not yet
/// exist.
/// </para>
/// </remarks>
public sealed class WorkerRoundRunner : INOpenCode
{
    /// <summary>
    /// What a round runs in its container while the factory has no agent to run (#9).
    /// It is a script rather than a command list because a round is several steps, and it
    /// runs under <c>bash -c</c> through the image's own recording wrapper so each step
    /// is recorded the way every other command in a round is.
    /// </summary>
    /// <remarks>
    /// Nothing here decides what the project's tests are (ADR-0011). The round is handed
    /// its repository, its brief and its tree, and says what it found — which is all this
    /// ticket claims.
    /// </remarks>
    public const string RoundScript = """
        set -uo pipefail
        cd "$AGENT_FACTORY_WORK"
        printf '%s' "$AGENT_FACTORY_BRIEF" > "${AGENT_FACTORY_OUT:-/out}/brief.md"
        run -o 'the brief this round was given' cat "${AGENT_FACTORY_OUT:-/out}/brief.md"
        run -o 'the tree this round was given' git log --oneline -1
        run -o 'the tree this round left behind' git status --porcelain
        """;

    private readonly ContainerRuntime _containers;
    private readonly ProjectLoadReport _projects;
    private readonly FactoryOptions _options;
    private readonly ILogger<WorkerRoundRunner> _logger;

    public WorkerRoundRunner(
        ContainerRuntime containers,
        ProjectLoadReport projects,
        FactoryOptions options,
        ILogger<WorkerRoundRunner> logger)
    {
        _containers = containers ?? throw new ArgumentNullException(nameof(containers));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<RoundResult> RunRoundAsync(Round round, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(round);

        var project = _projects.Projects.FirstOrDefault(candidate => candidate.Name == round.Project);
        if (project is null)
        {
            // The project file this work item was taken from is not being served any more.
            // That is a round that cannot start, not a round that failed, and it is
            // permanent rather than transient: configuration loads at start and does not
            // hot reload, so nothing about the second attempt would differ from the first.
            // Retrying it would spend two more containers to be told the same thing.
            _logger.LogWarning(
                "Round {Round} of {Project}#{Issue} cannot run: no project file is being served for {Project}, "
                    + "so there is no worker image to start from. Nothing about a second attempt would be different.",
                round.WorkItemId,
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
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // The tree arrives over the network because a worker container is given no
                // host path to put one in (ADR-0010).
                ["AGENT_FACTORY_REPO_URL"] = round.RepoUrl,
                ["AGENT_FACTORY_BASE_REF"] = round.BaseBranch,
                ["AGENT_FACTORY_BRIEF"] = round.Feedback,
            });

        WorkerContainerRun run;
        try
        {
            run = await _containers.RunAsync(
                request,
                landing,
                new RoundLog(_logger, round.WorkItemId),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The loop stopped waiting for this round. It has already recorded it as timed
            // out, and the container runtime has already removed the container: this is
            // the call ending, not a failure to report. Rethrown because a cancelled
            // operation says so, and because the loop asked for the round to stop.
            _logger.LogInformation(
                "Round {Round} of {Project}#{Issue} was ended by the factory, and its container removed with it.",
                round.WorkItemId,
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
                "Round {Round} of {Project}#{Issue} had no container to run in: {Reason}. "
                    + "The factory has read that as a {Classification} failure.",
                round.WorkItemId,
                round.Project,
                round.IssueNumber,
                broken.Message,
                broken.Class);

            return RoundResult.Failed(broken.Class);
        }

        _logger.LogInformation(
            "Round {Round} of {Project}#{Issue} finished; its result, its tree and its log are in {Landing}.",
            round.WorkItemId,
            round.Project,
            round.IssueNumber,
            landing);

        if (run.ResultFile is not { } resultFile)
        {
            // A round with no result is not a round with nothing: its log is the whole of
            // what there is to show, so it is logged here rather than lost. It is also
            // permanent, and deliberately so — the container ran, and whatever stopped it
            // from writing its result would stop a second container the same way. Note what
            // this is *not*: the container failing to start, which is the transient shape of
            // the same-looking failure and is classified above.
            _logger.LogWarning(
                "Round {Round} of {Project}#{Issue} came back without a result: it ran and produced nothing, "
                    + "so another attempt would only be told the same thing. Its log ends: {Log}",
                round.WorkItemId,
                round.Project,
                round.IssueNumber,
                run.LogTail);

            return RoundResult.Failed(FailureClass.Permanent);
        }

        var header = WorkerResultFile.ReadHeader(resultFile);
        if (header is null)
        {
            _logger.LogWarning(
                "Round {Round} of {Project}#{Issue} wrote a result file with nothing in it that can be read. Its log ends: {Log}",
                round.WorkItemId,
                round.Project,
                round.IssueNumber,
                run.LogTail);
        }

        return RoundResult.Produced(
            header ?? $"a result file with no readable header, at {resultFile}",
            WorkerResultFile.ReadNote(resultFile));
    }

    /// <summary>
    /// A round's log, line by line, as the round writes it. This is the live channel out
    /// of a round (ADR-0010) and the only place the factory's own logs say anything about
    /// what a round was doing while it was doing it.
    /// </summary>
    private sealed class RoundLog(ILogger logger, Guid workItemId) : IProgress<string>
    {
        public void Report(string value) =>
            logger.LogDebug("Round {Round} says: {Line}", workItemId, value);
    }
}
