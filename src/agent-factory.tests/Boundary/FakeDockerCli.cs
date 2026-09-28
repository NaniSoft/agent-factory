namespace AgentFactory.Tests.Boundary;

using AgentFactory.Containers;

/// <summary>
/// The Docker CLI, faked: it records every invocation verbatim and answers from a
/// handler a test wrote. It knows the two verbs whose behaviour the rest of the code
/// depends on — <c>cp</c> puts a file or a tree on the host, and <c>logs -f</c> can be
/// made to hang the way a stuck round does — so the container runtime can be driven
/// through every outcome without a daemon anywhere near the test.
/// </summary>
public sealed class FakeDockerCli : IDockerCli
{
    private readonly List<DockerCall> _calls = [];

    /// <summary>Every invocation, in order, with the token it was given.</summary>
    public IReadOnlyList<DockerCall> Calls => _calls;

    /// <summary>Every argument vector, in order. Shorthand for the shape assertions.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Argvs => _calls.Select(call => call.Arguments).ToList();

    /// <summary>Every line the fake reported to the caller's progress channel, in order.</summary>
    public List<string> Lines { get; } = [];

    /// <summary>
    /// The files the container is pretending to hold, by container path. A <c>cp</c> of a
    /// path that is a prefix of one of these lifts the tree under it, the way the real
    /// command does.
    /// </summary>
    public Dictionary<string, string> ContainerFiles { get; } = new(StringComparer.Ordinal);

    /// <summary>What an invocation answers. Defaults to a silent success.</summary>
    public Func<DockerCall, Task<DockerInvocation>> Handler { get; set; } =
        _ => Task.FromResult(new DockerInvocation(0, string.Empty));

    public Task<DockerInvocation> InvokeAsync(
        IReadOnlyList<string> arguments,
        IProgress<string>? output,
        CancellationToken cancellationToken)
    {
        var call = new DockerCall(arguments, output, cancellationToken);
        _calls.Add(call);
        return Answer(call);
    }

    /// <summary>The argv of the nth invocation, which is usually the one under test.</summary>
    public IReadOnlyList<string> Argv(int index) => _calls[index].Arguments;

    /// <summary>The single invocation of one verb, or a failure saying there was not exactly one.</summary>
    public IReadOnlyList<string> TheOnly(string verb)
    {
        var matching = Argvs.Where(argv => argv.Count > 0 && argv[0] == verb).ToList();
        return Assert.Single(matching);
    }

    private async Task<DockerInvocation> Answer(DockerCall call)
    {
        // `cp` puts something on the host, or fails the way the command fails when the
        // path is not there, because what is on the host afterwards is what the rest of
        // the factory reads. The fake behaves like the command rather than like a stub,
        // so a test that says a file came back is not a test that says the code
        // remembered a path.
        if (call.Arguments.FirstOrDefault() == "cp")
        {
            // `docker cp` takes `container:path` on the way out and a host path on the
            // way in. A container name cannot contain a colon, so the first one is the
            // boundary between them.
            var from = call.Arguments[1].Split(':', 2)[1];
            if (!Copy(from, call.Arguments[2]))
            {
                return new DockerInvocation(1, $"Error response from daemon: no such file or directory: {from}");
            }
        }

        return await Handler(call);
    }

    private bool Copy(string from, string to)
    {
        if (ContainerFiles.TryGetValue(from, out var whole))
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
            File.WriteAllText(to, whole);
            return true;
        }

        var tree = ContainerFiles
            .Where(file => file.Key.StartsWith(from.TrimEnd('/') + "/", StringComparison.Ordinal))
            .ToList();

        if (tree.Count == 0)
        {
            return false;
        }

        foreach (var (path, content) in tree)
        {
            var relative = path[from.TrimEnd('/').Length..].TrimStart('/');
            var destination = System.IO.Path.Combine(to, relative);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, content);
        }

        return true;
    }

    /// <summary>One invocation, with the channel it reported on and the token it was given.</summary>
    public sealed record DockerCall(
        IReadOnlyList<string> Arguments,
        IProgress<string>? Output,
        CancellationToken CancellationToken);
}
