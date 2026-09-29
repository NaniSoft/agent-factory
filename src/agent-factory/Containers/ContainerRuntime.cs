namespace AgentFactory.Containers;

using Microsoft.Extensions.Logging;

/// <summary>
/// One worker container, from creation to removal. It starts a container from an image,
/// lets it run, streams its log, lifts the round's one result file and its tree out with
/// <c>docker cp</c>, and takes the container away afterwards — which is the whole of the
/// factory's knowledge of Docker, and it is deliberately that small.
/// </summary>
/// <remarks>
/// <para>
/// A container's lifetime and a round's boundary are the same thing (ADR-0001), so the
/// removal is in a <c>finally</c> and not on the success path. Success, a round that wrote
/// no result, a container that never started, a caller that cancelled, an exception out of
/// the log stream: every one of them removes the container, because a container that
/// outlives its round is exactly what makes "nothing survives it" untrue and leaks the
/// compensating controls ADR-0012 names.
/// </para>
/// <para>
/// The removal addresses the container by <em>name</em> and runs on a token nobody can
/// cancel. A name is known before the container exists, so a create whose own output was
/// lost can still be undone; and a token that has just been cancelled must not be able to
/// stand between a finished round and a removed container.
/// </para>
/// <para>
/// Nothing here sleeps, defers or times out. The one thing this waits for is a container,
/// and the wait is ended by the caller's token rather than by a clock of its own.
/// </para>
/// </remarks>
public sealed class ContainerRuntime
{
    /// <summary>Where the image writes the round's one result file, outside the working tree.</summary>
    public const string ResultPathInContainer = "/out/result.json";

    /// <summary>Where the image puts the repository the round fetched.</summary>
    public const string WorkPathInContainer = "/work";

    /// <summary>
    /// The folder inside a round's landing directory that the round's tree is lifted into.
    /// Named here rather than written out at each end, because two components now have to
    /// agree on it: this runtime, which puts the tree there, and the merger, which reads
    /// the commit out of it and pushes it (ADR-0006).
    /// </summary>
    public const string RoundTreeFolder = "tree";

    /// <summary>How much of a finished round's log is kept for the log line that follows it.</summary>
    private const int LogTailBytes = 8192;

    private readonly IDockerCli _docker;
    private readonly ILogger<ContainerRuntime> _logger;

    public ContainerRuntime(IDockerCli docker, ILogger<ContainerRuntime> logger)
    {
        _docker = docker ?? throw new ArgumentNullException(nameof(docker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Runs one container's command from end to end, and lifts out what it left. The
    /// container is removed on every way out of this method, including a cancellation.
    /// </summary>
    /// <param name="request">The image, the command, and the round's environment.</param>
    /// <param name="landing">
    /// A host directory the lifted-out files are written into, created if it is not there.
    /// It is the caller's directory and the caller keeps it: the host needs the round's
    /// tree after the container that made it is gone, because the host pushes (ADR-0006).
    /// </param>
    /// <param name="log">Where the round's log goes as the round runs it, if anywhere.</param>
    /// <param name="cancellationToken">The round's own token. Ending it ends the round.</param>
    public async Task<WorkerContainerRun> RunAsync(
        WorkerContainerRequest request,
        string landing,
        IProgress<string>? log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Image);
        ArgumentException.ThrowIfNullOrWhiteSpace(landing);

        Directory.CreateDirectory(landing);

        // The tail is kept whether or not anybody is listening. It used to be fed only when
        // a caller asked for progress, which made <see cref="WorkerContainerRun.LogTail"/>
        // silently empty for a caller that wanted nothing streamed — and the round runner
        // puts that tail on the round's result, so a round whose result cannot be read would
        // have degraded to showing nothing at all (story 29). Keeping the tail and passing
        // the caller's channel on to the same object is what makes the two impossible to
        // differ.
        var tail = new LogTail(LogTailBytes);
        var channel = new Tee(tail, log);

        try
        {
            await CreateAsync(request, cancellationToken);
            await StartAsync(request.Name, cancellationToken);
            await FollowTheLogAsync(request.Name, channel, cancellationToken);

            // The container has stopped, so both channels out are closed: the log we just
            // followed, and the files below. There is no third one (ADR-0010).
            var result = await LiftAsync(
                request.Name,
                ResultPathInContainer,
                Path.Combine(landing, "result.json"),
                cancellationToken);

            var tree = await LiftAsync(
                request.Name,
                WorkPathInContainer,
                Path.Combine(landing, RoundTreeFolder),
                cancellationToken);

            return new WorkerContainerRun(result, tree, tail.Text);
        }
        finally
        {
            // Unconditional, uncancellable, and after everything above — including a
            // throw. This is the line the whole of ADR-0001's "nothing survives a round"
            // rests on, and the comment above it is the reason it is not on the happy path.
            await RemoveAsync(request.Name, tail);
        }
    }

    private async Task CreateAsync(WorkerContainerRequest request, CancellationToken cancellationToken)
    {
        // No -p, no -P, no -v, no --mount, no --network, no --privileged, and nothing that
        // would take a host path or open a port. These are properties of this command and
        // not of the image: an image that exposes nothing can still be published, and an
        // image with no VOLUME can still be handed one. The round fetches its repository
        // over the network because the alternative is a host path inside an untrusted box
        // (ADR-0010, ADR-0012).
        var create = new List<string> { "create", "--name", request.Name };

        foreach (var (name, value) in request.Environment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            create.Add("--env");
            create.Add($"{name}={value}");
        }

        create.Add(request.Image);
        create.AddRange(request.Command);

        await ExpectAsync(create, null, cancellationToken, "create");
    }

    private async Task StartAsync(string name, CancellationToken cancellationToken) =>
        await ExpectAsync(["start", name], null, cancellationToken, "start");

    /// <summary>
    /// Reads the round's log as the round writes it, and blocks until the container stops.
    /// A log that cannot be read ends the round rather than being shrugged off: the
    /// alternative is to carry on, lift a result out of a container that is still running,
    /// and then take that container away a moment later — a round silently cut short and
    /// reported as if it had finished.
    /// </summary>
    private async Task FollowTheLogAsync(string name, IProgress<string>? log, CancellationToken cancellationToken) =>
        await ExpectAsync(["logs", "-f", name], log, cancellationToken, "logs");

    /// <summary>
    /// Lifts one path out of a stopped container onto the host. A <c>cp</c> that fails is
    /// a path that is not there, not an error: a round whose result is missing still has a
    /// log, and a round must never be invisible (story 29).
    /// </summary>
    /// <remarks>
    /// There is deliberately no classification here, and this is the boundary that decides
    /// it. A <c>cp</c> that failed is one signal: a container that wrote no result file and
    /// a host that could not fetch one come back the same way, and nothing this side of the
    /// call can tell them apart without reading the reason out of the message — which is
    /// the guessing the whole retry policy refuses to do. So the lift says only that there
    /// is no file, and the round runner, which knows the round ran, calls it what it is.
    /// </remarks>
    private async Task<string?> LiftAsync(
        string name,
        string from,
        string to,
        CancellationToken cancellationToken)
    {
        var copied = await _docker.InvokeAsync(["cp", $"{name}:{from}", to], null, cancellationToken);
        if (copied.ExitCode == 0 && (File.Exists(to) || Directory.Exists(to)))
        {
            return to;
        }

        _logger.LogWarning(
            "The round's {From} did not come out of its container: {Reason}. The round's log is all there is left of it.",
            from,
            FirstLine(copied.Output));

        return null;
    }

    private async Task RemoveAsync(string name, LogTail tail)
    {
        // CancellationToken.None, deliberately. A round is very often torn down *because*
        // its token was cancelled, and handing that cancelled token to the removal would
        // make the one operation that must happen the one operation that cannot.
        var removed = await _docker.InvokeAsync(["rm", "-f", name], null, CancellationToken.None);
        if (removed.ExitCode != 0)
        {
            _logger.LogWarning(
                "The worker container {Container} could not be removed: {Reason}. It outlived its round, which is the one thing a round must not do. {Log}",
                name,
                FirstLine(removed.Output),
                tail.Text);
        }
    }

    private async Task ExpectAsync(
        IReadOnlyList<string> arguments,
        IProgress<string>? output,
        CancellationToken cancellationToken,
        string what)
    {
        var done = await _docker.InvokeAsync(arguments, output, cancellationToken);
        if (done.ExitCode == 0)
        {
            return;
        }

        throw new WorkerContainerException(what, FirstLine(done.Output));
    }

    private static string FirstLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() is { Length: > 0 } line
            ? line
            : "the command said nothing about it";

    /// <summary>
    /// Forwards every line the container printed while keeping the end of them. The whole
    /// of a round's log goes to the caller's channel; only the tail is kept, because a
    /// 90-minute build's output does not belong in memory or in a log line.
    /// </summary>
    /// <remarks>
    /// Synchronised because <see cref="IDockerCli"/> reports from both of a command's
    /// streams at once, so this is called from two threads at once. Unsynchronised it drops
    /// lines from a round's log and can throw while doing it, which is a defect in the one
    /// component whose whole job is to lose nothing.
    /// </remarks>
    private sealed class LogTail(int bytes)
    {
        private readonly Lock _lock = new();
        private readonly Queue<string> _lines = new();
        private int _length;

        public string Text
        {
            get
            {
                lock (_lock)
                {
                    return string.Join(Environment.NewLine, _lines);
                }
            }
        }

        public void Append(string line)
        {
            lock (_lock)
            {
                _lines.Enqueue(line);
                _length += line.Length + Environment.NewLine.Length;

                while (_length > bytes && _lines.Count > 1)
                {
                    _length -= _lines.Dequeue().Length + Environment.NewLine.Length;
                }
            }
        }
    }

    /// <summary>
    /// The round's log going to two places at once: the tail this runtime keeps, and the
    /// channel the caller asked for — which is optional, because a caller that wants
    /// nothing streamed still wants the tail on the result. One class rather than two
    /// subscribers, so the tail cannot come to hold a different set of lines than the
    /// caller was given.
    /// </summary>
    private sealed class Tee(LogTail tail, IProgress<string>? to) : IProgress<string>
    {
        public void Report(string value)
        {
            tail.Append(value);
            to?.Report(value);
        }
    }
}
