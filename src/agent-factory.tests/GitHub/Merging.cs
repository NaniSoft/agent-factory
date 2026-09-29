namespace AgentFactory.Tests.GitHub;

using AgentFactory.Containers;
using AgentFactory.Credentials;
using AgentFactory.GitHub;
using AgentFactory.Projects;
using AgentFactory.WorkItems;
using AgentFactory.Tests.Boundary;
using AgentFactory.Tests.Review;
using Microsoft.Extensions.Logging;

/// <summary>
/// The real client, over a faked transport, against a real tree on this host and a real
/// bare repository to push to. Everything but the network is the production thing.
/// </summary>
/// <remarks>
/// <para>
/// The awkward part of building this is the path. The merger finds a round's tree the only
/// way it can — under <see cref="FactoryOptions.RoundsDirectory"/>, named for the work
/// item, in the folder the container runtime lifts into — so the fixture builds the tree
/// exactly there rather than teaching the client a second way to find one. That is the
/// point of the arrangement: a test cannot pass by pointing the merger at a tree it put
/// somewhere convenient.
/// </para>
/// <para>
/// The object files are made read-only, as #11 established, because that is what a tree
/// lifted out of a Linux container looks like on a Windows host and the push is a read of
/// them. A push that needed to write into the tree would pass here and fail in production.
/// </para>
/// </remarks>
public sealed class Merging : IDisposable
{
    /// <summary>
    /// A value shaped like a token and unmistakably not one, so that "the credential is
    /// never logged" is a check on a string nobody could confuse for anything else.
    /// </summary>
    public const string Token = "ghp_thisisnotarealtoken0123456789abcdef";

    public const string KeyName = "NEXUS_GITHUB_TOKEN";

    private readonly FactoryRoot _root = FactoryRoot.Create();
    private readonly TestClock _clock = new();
    private readonly FakeCredentialReader _credentials;
    private readonly LiftedTree _tree;
    private readonly BareRemote _remote = BareRemote.Create();

    public Merging(int issueNumber = 42, string issueTitle = "The poller stops when a project is paced")
    {
        Root = _root;
        Options = new FactoryOptions(
            _root.FactoriesDirectory,
            _root.DatabasePath,
            new Uri("http://127.0.0.1:0"));

        Store = new SqliteWorkItemStore(_root.DatabasePath, _clock);
        WorkItem = Store
            .Intake("nexus", _remote.Path, issueNumber, issueTitle, "What the issue says.", "main")
            .WorkItem;

        // Where the round runner would have lifted it, and what a round leaves there: a
        // commit on top of the base, with its object files read-only.
        //
        // Per round, not per work item: `docker cp` copies a directory into a destination
        // that already exists rather than replacing it, so a work item's rounds cannot share
        // one landing directory without the second landing inside the first (#22).
        _tree = LiftedTree.WithACommitOn("main", TreePath);

        _tree.Committed("the change a round made", ("src/Poller.cs", "public sealed class Poller { }\n"));
        _tree.WithReadOnlyObjects();

        // The round, recorded against that tree. This is what the merger now reads: the
        // path is on the round's own record rather than recomputed, so the tree the merger
        // ships is the tree the board showed a reviewer.
        Round = Store.RecordRound(
            WorkItem.Id,
            AgentFactory.Rounds.RoundOutcome.Produced,
            resultPayload: "the round's own record of itself",
            agentNote: "Made the poller keep going when a project is paced.",
            startedUtc: _clock.UtcNow,
            diff: new AgentFactory.Results.HostDiff(
                Text: "diff --git a/src/Poller.cs b/src/Poller.cs\n",
                Base: "refs/remotes/origin/main",
                Tree: TreePath,
                ContainerBounded: false,
                UnavailableBecause: null));

        _credentials = new FakeCredentialReader().Having(KeyName, Token);

        var project = new Project(
            Name: "nexus",
            RepoUrl: _remote.Path,
            WorkerImage: "ghcr.io/nanisoft/agent-factory-worker:1",
            LlmProvider: "anthropic",
            GitHubKeyName: KeyName,
            LlmKeyName: "NEXUS_ANTHROPIC_API_KEY",
            SourceFile: "nexus.yaml");

        Client = new GitHubClient(
            new HttpClient(Api) { BaseAddress = new Uri("https://api.github.com/") },
            new ProjectLoadReport([project], []),
            Options,
            Store,
            _credentials,
            Log);
    }

    public GitHubApi Api { get; } = new();

    /// <summary>The store's clock, so a test recording another round can use the same one.</summary>
    public TestClock Clock => _clock;

    public RecordingLogger<GitHubClient> Log { get; } = new();

    public GitHubClient Client { get; }

    public IWorkItemStore Store { get; }

    public WorkItem WorkItem { get; }

    /// <summary>
    /// The round whose change this is, as the store holds it — the record the merger reads
    /// the tree's path from and the board renders it on.
    /// </summary>
    public AgentFactory.WorkItems.RoundResultRecord Round { get; }

    public FactoryOptions Options { get; }

    public FactoryRoot Root { get; }

    public BareRemote Remote => _remote;

    /// <summary>Every name the credential reader was asked for, in order.</summary>
    public IReadOnlyList<string> CredentialsAsked => _credentials.Asked;

    /// <summary>
    /// The round's tree, as it landed.
    /// </summary>
    /// <remarks>
    /// Built under <see cref="FactoryOptions.RoundsDirectory"/>, named for the work item and
    /// the round inside it, because that is the shape the round runner now lifts into and
    /// because the merger finds a tree the only way it can — off the round's own record,
    /// which is the same path the board prints for that round (#22). The fixture therefore
    /// records the round rather than arranging for the client to look somewhere convenient,
    /// and a test cannot pass by pointing the merger at a tree it put somewhere else.
    /// </remarks>
    public LiftedTree Tree => _tree;

    /// <summary>The commit the merger will push, read from git rather than remembered.</summary>
    public string Commit => _tree.Head();

    /// <summary>
    /// The branch the merger will push it to.
    /// </summary>
    /// <remarks>
    /// Spelled out here rather than read from <see cref="BranchName"/> on purpose. The
    /// branch name is a thing a human sees in a repository and the spec names it, so the
    /// tests that care about it assert the literal string; a fixture that computed the
    /// answer with the same rule as the code under test would agree with a wrong rule.
    /// The tests that do not care — most of the failure cases — use this, where the exact
    /// spelling is not what is under test.
    /// </remarks>
    public string Branch => "agent-factory/42-the-poller-stops-when-a-project-is-paced";

    /// <summary>
    /// Where the round's tree really is: the path on the round's own record, which is where
    /// the round runner lifts into and therefore where the merger looks.
    /// </summary>
    public string TreePath => Path.Combine(
        Options.RoundsDirectory,
        WorkItem.Id.ToString("N"),
        "round-1",
        "attempt-1",
        AgentFactory.Containers.ContainerRuntime.RoundTreeFolder);

    /// <summary>Ships the work item's change, once.</summary>
    public Task Merge(CancellationToken cancellationToken = default) =>
        Client.MergeAsync(WorkItem.RepoUrl, WorkItem.IssueNumber, cancellationToken);

    /// <summary>
    /// Answers for a change that has not been pushed and has no pull request: nothing
    /// there, a branch that is not there, and room to open one.
    /// </summary>
    public GitHubApi NothingShippedYet()
    {
        Api.Responding("GET", "/pulls?", (System.Net.HttpStatusCode.OK, "[]"));
        Api.Responding("GET", "/git/ref/heads/", System.Net.HttpStatusCode.NotFound, """{"message": "Not Found"}""");

        return Api;
    }

    public void Dispose()
    {
        _tree.Dispose();
        _remote.Dispose();
        _root.Dispose();
    }
}
