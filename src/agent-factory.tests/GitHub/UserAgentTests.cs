namespace AgentFactory.Tests.GitHub;

using System.Net;
using AgentFactory.Failures;
using AgentFactory.GitHub;
using AgentFactory.Projects;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;
using Microsoft.Extensions.Logging;

/// <summary>
/// The header GitHub refuses a request without, and the test that exists because 317 tests
/// did not catch its absence.
/// </summary>
/// <remarks>
/// <para>
/// #16 ran this factory against the real GitHub API for the first time, and every call it
/// made came back with a 403 whose body said the request had no <c>User-Agent</c>. There
/// was no intake and no merge, and there never had been: the entire external surface of
/// this system had never worked, and the suite was green throughout.
/// </para>
/// <para>
/// <strong>The reason it was green is the thing this file is about.</strong> The seam is
/// <see cref="HttpMessageHandler"/>, so the client really ran — its ordering, its
/// classification and its git invocations — against a fake that recorded requests and
/// reproduced nothing about what github.com will accept. A faked transport cannot catch a
/// requirement of the real service, because a fake that agreed with the client by
/// construction would have agreed with a broken one just as happily. So the claim here is
/// about the wire: what the transport was handed, read off the request itself, over every
/// shape of request the client can send.
/// </para>
/// <para>
/// And because a guard that never fires is not a check, <see cref="GitHubApi"/> now refuses
/// a request with no <c>User-Agent</c> the way GitHub does, so every test in this layer
/// would fail without the header rather than only these two. The second test is what keeps
/// that guard honest.
/// </para>
/// </remarks>
public class UserAgentTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";

    [Fact]
    public async Task Every_request_the_client_makes_carries_a_user_agent()
    {
        // Both halves of the seam, because "every request" means every request: intake's
        // two reads and the merger's five calls. A test that only polled would have passed
        // against a client that identified itself to GitHub and not to itself, and a test
        // that only merged would have passed against one that could ship nothing and so
        // never had to say who it was.
        var sent = new List<GitHubApi.Recorded>();

        // Intake: the open issues, and the default branch they are built against.
        using var reads = new GitHubApi();
        reads.Responding("GET", "/issues?", (HttpStatusCode.OK, """[{ "number": 1, "title": "An issue" }]"""));
        reads.Responding("GET", "repos/NaniSoft/nexus", (HttpStatusCode.OK, """{"default_branch": "main"}"""));

        var reader = ClientOver(reads);
        await reader.ListOpenIssuesAsync(RepoUrl, CancellationToken.None);
        await reader.GetDefaultBranchAsync(RepoUrl, CancellationToken.None);
        sent.AddRange(reads.Requests);

        // And the merger, over a real tree and a real bare repository: find the pull
        // request, ask where the branch is, open one, and merge it.
        using var merging = new Merging();
        merging.NothingShippedYet();
        merging.Api.Responding(
            "POST", "/pulls", (HttpStatusCode.Created, GitHubApi.PullRequest(7, merging.Commit, merging.Branch)));
        merging.Api.Responding("PUT", "/merge", (HttpStatusCode.OK, """{"merged": true}"""));

        await merging.Merge();
        sent.AddRange(merging.Api.Requests);

        // Every shape of request the client can send was actually sent, so this is not a
        // pass on a set of one. Written out rather than counted: a count would be
        // satisfied by one request repeated, which proves nothing about the others.
        Assert.Equal(
            [
                "GET branch ref",
                "GET issues",
                "GET pull requests",
                "GET repository",
                "POST pull request",
                "PUT merge",
            ],
            sent.Select(KindOf).Distinct().Order(StringComparer.Ordinal).ToList());

        // **The claim.** Read off the request the transport was handed, not off the
        // client's configuration: what goes on the wire is the only thing GitHub has ever
        // cared about.
        Assert.All(sent, request => Assert.False(
            string.IsNullOrWhiteSpace(request.UserAgent),
            $"{request.Shape} went out with no User-Agent, and github.com refuses every request that has not got one"));

        // And it is this factory naming itself, so a GitHub administrator looking at an
        // access log can tell whose client it was.
        Assert.All(sent, request => Assert.Equal(GitHubClient.UserAgent, request.UserAgent));

        // The other two headers are still on every request, because a fix that took one out
        // to put another in would be a trade rather than a fix.
        Assert.All(sent, request =>
        {
            Assert.Equal(GitHubClient.ApiVersion, request.Headers["X-GitHub-Api-Version"]);
            Assert.Contains("application/vnd.github+json", request.Headers["Accept"], StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_request_with_no_user_agent_is_refused_exactly_as_github_refuses_it()
    {
        // The fake's guard, tested, because a fake that refuses nothing is the mistake this
        // ticket exists to correct and it would be the same mistake moved into the test
        // layer: a guard that never fires looks exactly like a guard that is not there.
        using var api = new GitHubApi();
        api.Responding("GET", "repos/NaniSoft/nexus", (HttpStatusCode.OK, """{"default_branch": "main"}"""));

        using var http = new HttpClient(api) { BaseAddress = new Uri("https://api.github.com/") };

        // Bare: what the client did for its whole first life.
        using var refused = await http.GetAsync("repos/NaniSoft/nexus");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("User-Agent", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // And the same request with one is answered, so the refusal above was the header and
        // not the fake refusing everything.
        using var request = new HttpRequestMessage(HttpMethod.Get, "repos/NaniSoft/nexus");
        request.Headers.UserAgent.Add(NewAgent());

        using var answered = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);

        // And the client, which is what the guard is for, is not the thing that is refused
        // — because it now says who it is.
        var branch = await ClientOver(api).GetDefaultBranchAsync(RepoUrl, CancellationToken.None);
        Assert.Equal("main", branch);
    }

    [Fact]
    public async Task A_request_github_refuses_for_the_wrong_reason_is_still_classified_as_permanent()
    {
        // What the refusal is *for* once it arrives, because a header that fixes the request
        // and a classification that mishandles the answer is only half a fix. A 403 with no
        // rate-limit header is GitHub refusing on its own terms — wrong scope, SSO, branch
        // protection, or a missing User-Agent — and none of those is worth asking again.
        using var api = new GitHubApi();
        using var http = new HttpClient(api) { BaseAddress = new Uri("https://api.github.com/") };
        using var request = new HttpRequestMessage(HttpMethod.Get, "repos/NaniSoft/nexus");
        request.Headers.UserAgent.Add(NewAgent());

        using var refused = await http.SendAsync(request);

        var thrown = GitHubResponse.Refusal(
            refused.StatusCode,
            refused.Headers,
            await refused.Content.ReadAsStringAsync(),
            "reading a repository");

        Assert.IsType<PermanentFailure>(thrown);
        Assert.Equal(FailureClass.Permanent, Failures.Classify(thrown));
    }

    /// <summary>
    /// A <c>User-Agent</c> put on a request this file wrote rather than one the client built
    /// — so a test that wants to be refused can be, which is the only way the fake's guard
    /// can be shown to fire. The client's own name with a version on it, because the guard
    /// refuses a request with *no* header and nothing else.
    /// </summary>
    /// <remarks>
    /// Name and version as two arguments rather than one string: the single-argument
    /// constructor takes a <em>comment</em>, and a product token is what a header carries.
    /// The client does not go through this type at all — it adds the value without
    /// validation, for the reason its own line says.
    /// </remarks>
    private static System.Net.Http.Headers.ProductInfoHeaderValue NewAgent() =>
        new(GitHubClient.UserAgent, "1");

    /// <summary>
    /// The real client over a faked transport, serving one project with one credential.
    /// The same arrangement <c>GitHubReadTests</c> uses, kept here so this file's claim is
    /// about the client's own requests rather than about a fixture.
    /// </summary>
    private static GitHubClient ClientOver(GitHubApi api) => new(
        new HttpClient(api) { BaseAddress = new Uri("https://api.github.com/") },
        new ProjectLoadReport(
            [new Project("nexus", RepoUrl, "image", "anthropic", "NEXUS_GITHUB_TOKEN", "NEXUS_LLM_KEY", "nexus.yaml")],
            []),
        new FactoryOptions("factories", "data/agent-factory.db", new Uri("http://127.0.0.1:0")),
        new SqliteWorkItemStore(
            Path.Combine(Path.GetTempPath(), $"agent-factory-user-agent-{Guid.NewGuid():n}.db"),
            new TestClock()),
        new FakeCredentialReader().Having("NEXUS_GITHUB_TOKEN", Merging.Token),
        new RecordingLogger<GitHubClient>());

    /// <summary>
    /// Which of the client's request shapes this is, by the endpoint it addresses rather
    /// than by its method — a GET is four different requests here, and "every request"
    /// means each of them rather than each verb.
    /// </summary>
    private static string KindOf(GitHubApi.Recorded request)
    {
        var (method, path) = (request.Method, request.PathAndQuery);

        return (method, path) switch
        {
            ("GET", var p) when p.Contains("/issues?", StringComparison.Ordinal) => "GET issues",
            ("GET", var p) when p.Contains("/pulls?", StringComparison.Ordinal) => "GET pull requests",
            ("GET", var p) when p.Contains("/git/ref/heads/", StringComparison.Ordinal) => "GET branch ref",
            ("GET", _) => "GET repository",
            ("POST", _) => "POST pull request",
            ("PUT", _) => "PUT merge",
            _ => throw new Xunit.Sdk.XunitException($"{method} {path} is a request shape this test does not know about"),
        };
    }
}
