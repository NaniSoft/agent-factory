namespace AgentFactory.Tests.GitHub;

using AgentFactory.Application.Projects;
using AgentFactory.Application;
using AgentFactory.Domain.Projects;
using AgentFactory.Application.GitHub;
using AgentFactory.Infrastructure.GitHub;
using AgentFactory.Tests.Boundary;
using AgentFactory.Infrastructure.WorkItems;
using AgentFactory.Application.WorkItems;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

/// <summary>
/// The shape of the relationship calls, as what the factory actually put on the wire: a
/// questions issue made a child of the issue that asked for it, and a blocking edge
/// between two issues.
/// </summary>
/// <remarks>
/// These are the tests that did not exist when the sub-issue link first ran, and their
/// absence is the story. The faked transport answered whatever the client sent, the loop
/// tests stand in for <c>IGitHub</c> whole, and so the first real Grill on a real repository
/// published its questions issue and then had the link refused with GitHub's own 404 — the
/// client had addressed the endpoint by the child's number and named the parent by the
/// string length of a node id, and no test could see either. GitHub's contract for these
/// endpoints is documentation rather than observation — the path carries the parent's issue
/// number, the body carries a database id — and a fake cannot know which id "exists"
/// without becoming GitHub. So the constraint is pinned here as the documented shape, with
/// the id fixtures written by hand rather than derived from the code under test, and the
/// transport's own guard refuses the link's skeleton before any scripted route is asked.
/// </remarks>
public class SubIssueLinkTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";

    /// <summary>
    /// A database id written by hand, in the range real GitHub issue ids live in — not
    /// derived from anything the client computes, so an answer built from the code under
    /// test cannot agree with this one by construction.
    /// </summary>
    private const long ParentDatabaseId = 3_124_598_761;

    /// <summary>The created issue's database id, as the scripted create answers it.</summary>
    private const long CreatedDatabaseId = 3_124_601_104;

    /// <summary>A second issue's database id, for the edge that links two existing issues.</summary>
    private const long ChildDatabaseId = 3_124_605_559;

    /// <summary>The blocker's database id, for the dependency edge.</summary>
    private const long BlockerDatabaseId = 3_124_610_002;

    private readonly GitHubApi _api = new();

    private readonly GitHubClient _client;

    private readonly FactoryRoot _root = FactoryRoot.Create();

    public SubIssueLinkTests()
    {
        var project = new Project(
            Name: "nexus",
            RepoUrl: RepoUrl,

            LlmModel: "anthropic/claude-sonnet-4-5",
            GitHubKeyName: "NEXUS_GITHUB_TOKEN",
            LlmKeyName: "NEXUS_ANTHROPIC_API_KEY",
            SourceFile: "nexus.yaml");

        _client = new GitHubClient(
            new HttpClient(_api) { BaseAddress = new Uri("https://api.github.com/") },
            new ProjectLoadReport([project], []),
            new FakeCredentialReader().Having("NEXUS_GITHUB_TOKEN", Merging.Token),
            new RecordingLogger<GitHubClient>());
    }

    [Fact]
    public async Task A_created_issue_is_linked_under_the_parents_number_by_its_database_id()
    {
        // The link route is scripted before the create route because both are POSTs matched
        // by path fragment and the link's path contains the create's fragment: first match
        // wins, so order here is what keeps the two answers from crossing.
        _api.Responding("POST", "/sub_issues", (HttpStatusCode.Created, "{}"));
        _api.Responding(
            "POST",
            "/repos/NaniSoft/nexus/issues",
            (HttpStatusCode.Created,
                $$"""{ "id": {{CreatedDatabaseId}}, "number": 7, "html_url": "{{RepoUrl}}/issues/7" }"""));
        _api.Responding(
            "GET",
            "/repos/NaniSoft/nexus/issues/3",
            (HttpStatusCode.OK,
                $$"""{ "id": {{ParentDatabaseId}}, "number": 3, "node_id": "MDU6SXNzdWUzMTI0NTk4NzYx" }"""));

        await _client.CreateIssueAsync(
            new NewIssue(RepoUrl, ParentNumber: 3, "Questions: #3", "What the issue asks."),
            CancellationToken.None);

        // The link, on the wire: the parent's number in the path, the child's database id
        // in the body. The parent id above is deliberately never asserted — the endpoint
        // does not take it, and a test asserting it would be asserting the client's own
        // detour back to itself.
        var link = Assert.Single(
            _api.Requests,
            request => request.PathAndQuery.EndsWith("/sub_issues", StringComparison.Ordinal));
        Assert.Equal("POST /repos/NaniSoft/nexus/issues/3/sub_issues", link.Shape);
        Assert.Equal(CreatedDatabaseId, link.Json().GetProperty("sub_issue_id").GetInt64());

        // And the parent was read before anything was created, so a parent this token
        // cannot see is a refusal before the orphan exists rather than after.
        var shapes = _api.Shapes;
        Assert.True(
            shapes.FindIndex(shape => shape.StartsWith("POST", StringComparison.Ordinal))
            > shapes.FindIndex(shape => shape.Contains("GET /repos/NaniSoft/nexus/issues/3", StringComparison.Ordinal)),
            "the parent must be read before the create");
    }

    [Fact]
    public async Task A_sub_issue_made_of_two_existing_issues_takes_both_identifiers_from_their_reads()
    {
        _api.Responding(
            "GET",
            "/repos/NaniSoft/nexus/issues/3",
            (HttpStatusCode.OK, $$"""{ "id": {{ParentDatabaseId}}, "number": 3 }"""));
        _api.Responding(
            "GET",
            "/repos/NaniSoft/nexus/issues/9",
            (HttpStatusCode.OK, $$"""{ "id": {{ChildDatabaseId}}, "number": 9 }"""));
        _api.Responding("POST", "/sub_issues", (HttpStatusCode.Created, "{}"));

        await _client.MakeASubIssueAsync(RepoUrl, parentNumber: 3, childNumber: 9, CancellationToken.None);

        var link = Assert.Single(
            _api.Requests,
            request => request.PathAndQuery.EndsWith("/sub_issues", StringComparison.Ordinal));
        Assert.Equal("POST /repos/NaniSoft/nexus/issues/3/sub_issues", link.Shape);
        Assert.Equal(ChildDatabaseId, link.Json().GetProperty("sub_issue_id").GetInt64());
    }

    [Fact]
    public async Task A_blocking_edge_names_the_blocker_by_its_database_id()
    {
        _api.Responding(
            "GET",
            "/repos/NaniSoft/nexus/issues/3",
            (HttpStatusCode.OK, $$"""{ "id": {{ParentDatabaseId}}, "number": 3 }"""));
        _api.Responding(
            "GET",
            "/repos/NaniSoft/nexus/issues/9",
            (HttpStatusCode.OK, $$"""{ "id": {{BlockerDatabaseId}}, "number": 9 }"""));
        _api.Responding("POST", "/dependencies/blocked_by", (HttpStatusCode.Created, "{}"));

        await _client.MakeBlockedByAsync(RepoUrl, blockedNumber: 3, blockerNumber: 9, CancellationToken.None);

        var edge = Assert.Single(
            _api.Requests,
            request => request.PathAndQuery.Contains("/dependencies/", StringComparison.Ordinal));
        Assert.Equal("POST /repos/NaniSoft/nexus/issues/3/dependencies/blocked_by", edge.Shape);
        Assert.Equal(BlockerDatabaseId, edge.Json().GetProperty("issue_id").GetInt64());
    }

    /// <summary>
    /// The transport's own guard, in the idiom of the user-agent tests: a check that never
    /// firing looks exactly like a check that is not there, so a malformed skeleton is
    /// turned away with GitHub's own 404 while a well-formed one passes to the routes.
    /// </summary>
    [Fact]
    public async Task A_link_whose_skeleton_is_not_githubs_is_refused_before_any_route()
    {
        _api.Responding("POST", "/sub_issues", (HttpStatusCode.Created, "{}"));

        using var http = new HttpClient(_api) { BaseAddress = new Uri("https://api.github.com/") };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("agent-factory-tests");

        // No sub_issue_id in the body, and a parent "number" that is not a number: both
        // are refusals github.com itself answers before any routing a test could write.
        var noBody = await http.PostAsync(
            "/repos/NaniSoft/nexus/issues/3/sub_issues",
            new StringContent("""{"parent": {"id": 1}}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, noBody.StatusCode);
        Assert.Contains("sub-issues", await noBody.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var badPath = await http.PostAsync(
            "/repos/NaniSoft/nexus/issues/not-a-number/sub_issues",
            new StringContent("""{"sub_issue_id": 3124601104}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, badPath.StatusCode);

        // And the well-formed skeleton reaches the scripted route, so the guard is a
        // refusal of the malformed and not a refusal of the call.
        var wellFormed = await http.PostAsync(
            "/repos/NaniSoft/nexus/issues/3/sub_issues",
            new StringContent(
                $$"""{ "sub_issue_id": {{CreatedDatabaseId}} }""",
                System.Text.Encoding.UTF8,
                "application/json"));
        Assert.Equal(HttpStatusCode.Created, wellFormed.StatusCode);
    }
}
