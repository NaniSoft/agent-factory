namespace AgentFactory.Containers;

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

/// <summary>
/// The Docker CLI, as a process. Arguments are handed to it one at a time and never
/// through a shell, so nothing the factory passes — a reviewer's words, a repository URL,
/// a round's own command — can be read as shell syntax on the way in.
/// </summary>
/// <remarks>
/// <para>
/// Both streams are read as the process writes them rather than after it exits: a round's
/// log is the channel carrying live progress (ADR-0010), and a 90-minute round that
/// buffered its output until the end would be a round that said nothing for 90 minutes.
/// </para>
/// <para>
/// The process is killed when the caller's token fires, and then waited for, so a
/// cancelled round leaves no <c>docker</c> process behind holding a socket to the daemon.
/// Killing the local process is not the same as removing the container, which is why the
/// container's removal is the runtime's job and is not cancellable.
/// </para>
/// </remarks>
public sealed class DockerCli : IDockerCli
{
    /// <summary>The executable, resolved through <c>PATH</c>.</summary>
    public const string DefaultExecutable = "docker";

    /// <summary>How much of a command's output is kept for the answer to its caller.</summary>
    private const int TailBytes = 8192;

    private readonly string _executable;
    private readonly ILogger<DockerCli> _logger;

    public DockerCli(ILogger<DockerCli> logger)
        : this(DefaultExecutable, logger)
    {
    }

    public DockerCli(string executable, ILogger<DockerCli> logger)
    {
        _executable = string.IsNullOrWhiteSpace(executable) ? DefaultExecutable : executable;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DockerInvocation> InvokeAsync(
        IReadOnlyList<string> arguments,
        IProgress<string>? output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            throw new ArgumentException("a docker invocation needs a command", nameof(arguments));
        }

        var start = new ProcessStartInfo
        {
            FileName = _executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // The tail of what the command printed, bounded. Only the invocations whose answer
        // a caller has to read keep one: a round's log goes to the channel.
        var bounded = new Bounded(TailBytes);

        _logger.LogDebug("docker {Arguments}", string.Join(' ', arguments.Select(Quote)));

        using var process = new Process { StartInfo = start };

        try
        {
            if (!process.Start())
            {
                throw new WorkerContainerException(
                    $"could not start '{_executable}': the Docker CLI is not on this machine's PATH");
            }
        }
        catch (Exception failed) when (failed is not WorkerContainerException)
        {
            // A machine with no Docker is a deployment fact an operator can read, not a
            // Win32Exception from inside a process wrapper with nothing said about it. ADR-0012
            // makes the daemon's own state a thing the factory is expected to know about, so
            // it is named here rather than left to whoever reads the stack trace.
            throw new WorkerContainerException(
                $"could not start '{_executable}': there is no Docker CLI on this machine's PATH",
                failed);
        }

        // Nothing is ever written to a docker command, so the command is told so at once
        // rather than being left holding an open pipe it might wait on.
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The command exited before its stdin was closed. It is not running any more,
            // which is all closing stdin was for.
        }

        using var stdout = new StreamPump(process.StandardOutput, bounded, output);
        using var stderr = new StreamPump(process.StandardError, bounded, output);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The round's token fired. The container itself is removed by the runtime,
            // which cannot be cancelled; this is only the local CLI process, which would
            // otherwise sit on holding its end of a socket to the daemon.
            //
            // The kill and the reap below are the load-bearing part: without them a
            // cancelled round leaves this process behind, holding its end of a socket to
            // the daemon. `A_cancelled_invocation_leaves_no_process_behind` and
            // `A_cancelled_round_ends_the_call_and_leaves_no_container_running` both assert
            // the process is gone, and neither passes without them.
            await Stop(process).ConfigureAwait(false);
            Observe(stdout.Drained);
            Observe(stderr.Drained);
            throw;
        }

        await Task.WhenAll(stdout.Drained, stderr.Drained).ConfigureAwait(false);

        return new DockerInvocation(process.ExitCode, bounded.Text);
    }

    private static async Task Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // It exited between the check and the kill, which is the outcome we wanted.
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Waits out an exception on a task nobody is going to read.</summary>
    private static void Observe(Task task) =>
        _ = task.ContinueWith(
            read => _ = read.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static string Quote(string argument) =>
        argument.Any(character => char.IsWhiteSpace(character) || character == '"')
            ? $"\"{argument}\""
            : argument;

    /// <summary>Reads one stream line by line as the process writes it.</summary>
    private sealed class StreamPump(StreamReader reader, Bounded tail, IProgress<string>? output) : IDisposable
    {
        public Task Drained { get; } = Task.Run(async () =>
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                tail.Append(line);
                output?.Report(line);
            }
        });

        public void Dispose() => reader.Dispose();
    }

    /// <summary>
    /// A string builder that keeps only its last few kilobytes.
    /// </summary>
    /// <remarks>
    /// Two pumps append to this at once, one per stream, and a <see cref="StringBuilder"/>
    /// is not thread-safe. That is not a theoretical concern: unsynchronised, the two
    /// pumps interleave their writes, the builder's internal bookkeeping goes wrong, and a
    /// round's log tail throws <c>ArgumentOutOfRangeException</c> — or, worse, silently
    /// loses a line. The lock is what makes one builder the right answer here rather than
    /// one per stream, and the tail of two streams in one string is the more useful thing
    /// to have anyway.
    /// </remarks>
    /// <summary>A bounded log tail, line-aligned: it starts on a line boundary or not at all.</summary>
    private sealed class Bounded(int bytes)
    {
        private readonly StringBuilder _into = new();
        private readonly Lock _lock = new();

        public void Append(string line)
        {
            lock (_lock)
            {
                _into.Append(line).Append('\n');

                // Bounded by bytes, but cut on a line boundary. A tail that begins halfway
                // through a line is a tail whose first line is a lie about what the round
                // said, and a truncated result is better than a garbled one.
                while (_into.Length > bytes)
                {
                    var cut = _into.ToString().IndexOf('\n', _into.Length - bytes);
                    _into.Remove(0, cut + 1);
                }
            }
        }

        public string Text
        {
            get
            {
                lock (_lock)
                {
                    return _into.ToString();
                }
            }
        }
    }
}
