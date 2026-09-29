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

    /// <summary>
    /// Trees the container is holding as real directories on this host, by container path,
    /// copied out byte for byte. The round's own tree is a git repository with a packfile
    /// in it, and a fake that could only produce text files could not stand in for it —
    /// the host's diff is <c>git diff</c> against that directory, so what a <c>cp</c>
    /// leaves behind has to be a real repository to be worth anything (ADR-0006).
    /// </summary>
    public Dictionary<string, string> ContainerTrees { get; } = new(StringComparer.Ordinal);

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

    /// <summary>
    /// One <c>cp</c> out of the fake container, with the destination rules the real
    /// command has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// **The rule that matters is that <c>docker cp</c> never replaces a destination
    /// directory: it copies the source <em>into</em> it, under the source's own name.**
    /// Verified against the real daemon (#22):
    /// </para>
    /// <code>
    /// cp into a destination that does not exist:  tree/top.txt, tree/sub/file.txt
    /// cp into a destination that already exists:  tree/top.txt, tree/sub/file.txt,
    ///                                           tree/work/top.txt, tree/work/sub/file.txt
    /// </code>
    /// <para>
    /// A fake that <em>merged</em> instead of nesting passed every two-round test in this
    /// suite against filesystem behaviour no reviewer has ever been given, and that is how a
    /// round's second tree came to land inside its first: the board showed round 1's diff on
    /// round 2's card, and the merger read the same path. The fake is not a stub here; it
    /// is standing in for a command whose whole hazard is this.
    /// </para>
    /// <para>
    /// A destination that is an existing <em>file</em> is the one thing a real <c>cp</c>
    /// does replace, and it is what a round's result file lands on, so that case is
    /// reproduced too rather than left to whichever branch a test happened to take.
    /// </para>
    /// </remarks>
    private bool Copy(string from, string to)
    {
        // The source's own name, which is what a copy into an existing directory is named.
        var source = from.TrimEnd('/');
        var name = System.IO.Path.GetFileName(source);
        var isTree = ContainerTrees.ContainsKey(from)
            || ContainerFiles.Keys.Any(file => file.StartsWith(source + "/", StringComparison.Ordinal));

        if (isTree && System.IO.Directory.Exists(to))
        {
            to = System.IO.Path.Combine(to, name);
        }

        if (ContainerTrees.TryGetValue(from, out var realTree))
        {
            // Copied as a real directory, attributes and all, because that is what comes
            // out of a container: a Linux tree with read-only object files in it, which is
            // the shape the host's diff reader has to survive on a Windows host.
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
            CopyDirectory(realTree, to);
            return true;
        }

        if (ContainerFiles.TryGetValue(from, out var whole))
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
            File.WriteAllText(to, whole);
            return true;
        }

        var tree = ContainerFiles
            .Where(file => file.Key.StartsWith(source + "/", StringComparison.Ordinal))
            .ToList();

        if (tree.Count == 0)
        {
            return false;
        }

        foreach (var (path, content) in tree)
        {
            var relative = path[source.Length..].TrimStart('/');
            var destination = System.IO.Path.Combine(to, relative);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, content);
        }

        return true;
    }

    /// <summary>
    /// One real directory copied onto the host, read-only attributes and all. Attributes
    /// are the point rather than an accident: a tree lifted out of a Linux container
    /// arrives on Windows with read-only pack files, and the code that later reads or
    /// cleans such a tree has to cope with that. A fake that quietly normalised them
    /// would let a test pass against markup no reviewer would ever get.
    /// </summary>
    private static void CopyDirectory(string from, string to)
    {
        System.IO.Directory.CreateDirectory(to);

        foreach (var file in System.IO.Directory.EnumerateFiles(from))
        {
            var destination = System.IO.Path.Combine(to, System.IO.Path.GetFileName(file));
            File.Copy(file, destination);
            File.SetAttributes(destination, File.GetAttributes(file));
        }

        foreach (var directory in System.IO.Directory.EnumerateDirectories(from))
        {
            CopyDirectory(directory, System.IO.Path.Combine(to, System.IO.Path.GetFileName(directory)));
        }
    }

    /// <summary>One invocation, with the channel it reported on and the token it was given.</summary>
    public sealed record DockerCall(
        IReadOnlyList<string> Arguments,
        IProgress<string>? Output,
        CancellationToken CancellationToken);
}
