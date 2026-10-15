namespace AgentFactory.Infrastructure.Rounds;

using AgentFactory.Application.Rounds;
using AgentFactory.Domain.Failures;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

/// <summary>
/// The child process as a fact of this machine: a program off the <c>PATH</c>, started
/// with one argument per element, both streams read as they are written, and the whole
/// process tree killed and reaped when the caller's token fires.
/// </summary>
/// <remarks>
/// <para>
/// The arguments go through <see cref="ProcessStartInfo.ArgumentList"/> and never through
/// a shell, on the same reasoning the container boundary always had: a reviewer's words, a
/// branch name and a repository URL are all things a stranger controls, and none of them
/// may become shell syntax on the way in.
/// </para>
/// <para>
/// Both streams are pumped as the process writes them, because a ninety-minute round that
/// buffered its log would be a round that said nothing for ninety minutes. The tail is
/// bounded at the same 8 KB the container log kept, and for the same reason: the whole
/// output goes to <c>ILogger</c> as it happens, and the tail is what travels on the round's
/// result.
/// </para>
/// <para>
/// A known gap, stated rather than hidden: this is a process tree, not a container. When
/// <em>this</em> process dies without the chance to cancel anything — a crash, a power
/// loss — the child it started may outlive it, finish its work with nowhere to report, and
/// hold its working tree. The container runtime could find its orphans by name; a process
/// cannot be found after the fact by anything this factory recorded. Cancellation, the
/// ordinary path, kills the tree.
/// </para>
/// </remarks>
public sealed class ProcessCli : IProcessCli
{
    /// <summary>How much of a process's output travels on the result, in bytes.</summary>
    public const int TailBytes = 8 * 1024;

    public async Task<ProcessInvocation> InvokeAsync(
        ProcessRequest request,
        IProgress<string>? output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Arguments);

        var start = new ProcessStartInfo
        {
            FileName = request.Executable,
            WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in request.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (request.Environment is { } extra)
        {
            foreach (var (name, value) in extra)
            {
                start.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = start };

        try
        {
            if (!process.Start())
            {
                throw new PermanentFailure($"'{request.Executable}' could not be started");
            }
        }
        catch (Exception refused) when (refused is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
        {
            // The binary is either on this machine's PATH or it is not, and a second
            // attempt in ten seconds begins a second identical failure. Which component
            // observed it is the component that classifies it, and this is the observer.
            throw new PermanentFailure(
                $"there is no {request.Executable} on this machine's PATH: {refused.Message}");
        }

        var tail = new Bounded(TailBytes);

        var stdout = PumpAsync(process.StandardOutput, output, tail, cancellationToken);
        var stderr = PumpAsync(process.StandardError, output, tail, cancellationToken);

        try
        {
            // The round is not an interactive session, and a program that waited on stdin
            // would wait for ever. Nothing here writes to a child.
            process.StandardInput.Close();

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);

            return new ProcessInvocation(process.ExitCode, tail.Text);
        }
        catch (OperationCanceledException)
        {
            // The caller stopped waiting — most often the round timeout. The local
            // process is killed and reaped, its whole tree with it, so a cancelled round
            // leaves nothing holding a file or a socket. The reaping runs on a token
            // nobody can cancel, because this is the one operation that must happen.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception reaping) when (reaping is InvalidOperationException or IOException)
            {
                // It exited between the check and the kill, which is the outcome wanted.
            }

            throw;
        }
    }

    /// <summary>
    /// One stream, read line by line for as long as the process writes it, each line sent
    /// to the caller's progress and to the tail. Ends when the stream does.
    /// </summary>
    private static async Task PumpAsync(
        StreamReader reader,
        IProgress<string>? output,
        Bounded tail,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            output?.Report(line);
            tail.Append(line);
        }
    }

    /// <summary>
    /// The bounded end of a stream of lines. Old lines fall off the front, so what is kept
    /// is always what was written last.
    /// </summary>
    private sealed class Bounded(int capacity)
    {
        private readonly StringBuilder _text = new();
        private readonly object _lock = new();

        public void Append(string line)
        {
            lock (_lock)
            {
                _text.AppendLine(line);
                if (_text.Length > capacity)
                {
                    _text.Remove(0, _text.Length - capacity);
                }
            }
        }

        public string Text
        {
            get
            {
                lock (_lock)
                {
                    return _text.ToString();
                }
            }
        }
    }
}
