namespace AgentFactory.Tests.GitHub;

using System.Net;
using System.Text;
using AgentFactory.Credentials;
using AgentFactory.Failures;
using AgentFactory.GitHub;
using AgentFactory.Projects;
using AgentFactory.WorkItems;
using Microsoft.Extensions.Logging;
using AgentFactory.Tests.Boundary;

/// <summary>
/// Intake over the real client: the two reads the poller makes, and the two shapes of
/// GitHub's API that make them worth testing rather than assumed.
/// </summary>
public class GitHubReadTests
{
    private static GitHubClient ClientOver(GitHubApi api, string repoUrl, FakeCredentialReader? credentials = null) =>
        new(
            new HttpClient(api) { BaseAddress = new Uri("https://api.github.com/") },
            new ProjectLoadReport(
                [new Project("nexus", repoUrl, "image", "anthropic", "NEXUS_GITHUB_TOKEN", "NEXUS_LLM_KEY", "nexus.yaml")],
                []),
            new FactoryOptions("factories", "data/agent-factory.db", new Uri("http://127.0.0.1:0")),
            new SqliteWorkItemStore(
                Path.Combine(Path.GetTempPath(), $"agent-factory-github-read-{Guid.NewGuid():n}.db"),
                new TestClock()),
            credentials ?? new FakeCredentialReader().Having("NEXUS_GITHUB_TOKEN", Merging.Token),
            new RecordingLogger<GitHubClient>());

    [Fact]
    public async Task The_base_a_round_is_built_against_is_the_repositories_own_default_branch()
    {
        using var api = new GitHubApi();
        api.Responding("GET", "repos/NaniSoft/nexus", (HttpStatusCode.OK, """{"default_branch": "trunk"}"""));

        var branch = await ClientOver(api, "https://github.com/NaniSoft/nexus")
            .GetDefaultBranchAsync("https://github.com/NaniSoft/nexus", CancellationToken.None);

        Assert.Equal("trunk", branch);
    }

    [Fact]
    public async Task Every_open_issue_is_a_candidate_and_no_pull_request_is()
    {
        // The issues endpoint returns pull requests as well as issues, and a pull request
        // is not a piece of work: intakeing one would make the factory build against its
        // own output, and the work item it made would be one the board then tried to ship a
        // second time. ADR-0007 is about labels and assignees; this is the filter nobody
        // would have thought to add, and it is not a filter — it is GitHub telling the
        // difference between two kinds of object.
        using var api = new GitHubApi();
        api.Responding("GET", "/issues?", (HttpStatusCode.OK, """
            [
              { "number": 12, "title": "A thing", "body": "words",
                "labels": [ { "name": "ready-for-agent" } ],
                "assignees": [ { "login": "someone" } ] },
              { "number": 13, "title": "A pull request", "body": null,
                "pull_request": { "url": "https://api.github.com/repos/NaniSoft/nexus/pulls/13" } }
            ]
            """));

        var issues = await ClientOver(api, "https://github.com/NaniSoft/nexus")
            .ListOpenIssuesAsync("https://github.com/NaniSoft/nexus", CancellationToken.None);

        var issue = Assert.Single(issues);
        Assert.Equal(12, issue.Number);
        Assert.Equal("A thing", issue.Title);
        Assert.Equal("words", issue.Body);

        // Carried and never read, which is ADR-0007's own assertion: the seam is faithful to
        // what GitHub returns and the poller applies no filter whatever.
        Assert.Equal(["ready-for-agent"], issue.Labels);
        Assert.Equal(["someone"], issue.Assignees);
    }

    [Fact]
    public async Task A_null_issue_body_is_an_empty_one_and_not_a_crash()
    {
        // GitHub's `body` is null for an issue opened with no description, and a work item
        // whose brief is null is a brief the round cannot be handed.
        using var api = new GitHubApi();
        api.Responding("GET", "/issues?", (HttpStatusCode.OK, """[{ "number": 12, "title": "A thing" }]"""));

        var issues = await ClientOver(api, "https://github.com/NaniSoft/nexus")
            .ListOpenIssuesAsync("https://github.com/NaniSoft/nexus", CancellationToken.None);

        Assert.Equal(string.Empty, Assert.Single(issues).Body);
    }

    [Fact]
    public async Task Intake_reads_a_second_page_when_the_first_one_was_full()
    {
        // Not bounded by accident: one page is 100 issues, and a repository with 101 open
        // issues has 101 of them whether or not the first request filled up.
        using var api = new GitHubApi();
        var pages = 0;
        api.Responding("GET", "/issues?", request =>
        {
            pages++;
            return new GitHubApi.Answer(
                HttpStatusCode.OK,
                pages == 1 ? Issues(100, first: 1) : Issues(1, first: 101),
                []);
        });

        var issues = await ClientOver(api, "https://github.com/NaniSoft/nexus")
            .ListOpenIssuesAsync("https://github.com/NaniSoft/nexus", CancellationToken.None);

        Assert.Equal(101, issues.Count);
        Assert.Equal(2, api.TimesAsked("GET", "/issues?"));
        Assert.Equal(101, issues[^1].Number);
    }

    [Fact]
    public async Task Intake_stops_at_its_own_ceiling_and_says_that_it_did()
    {
        // A repository with more open issues than one pass will read must not make a poll
        // unbounded, and must not be passed off as a whole list: the first thousand read
        // and the rest unread is a different fact from "this repository has a thousand open
        // issues", and only one of them is true.
        using var api = new GitHubApi();
        api.Responding("GET", "/issues?", (HttpStatusCode.OK, Issues(GitHubClient.IssuePageSize, first: 1)));
        var log = new RecordingLogger<GitHubClient>();

        var client = new GitHubClient(
            new HttpClient(api) { BaseAddress = new Uri("https://api.github.com/") },
            new ProjectLoadReport(
                [new Project("nexus", "https://github.com/NaniSoft/nexus", "image", "anthropic", "NEXUS_GITHUB_TOKEN", "NEXUS_LLM_KEY", "nexus.yaml")],
                []),
            new FactoryOptions("factories", "data/agent-factory.db", new Uri("http://127.0.0.1:0")),
            new SqliteWorkItemStore(
                Path.Combine(Path.GetTempPath(), $"agent-factory-github-read-{Guid.NewGuid():n}.db"),
                new TestClock()),
            new FakeCredentialReader().Having("NEXUS_GITHUB_TOKEN", Merging.Token),
            log);

        var issues = await client.ListOpenIssuesAsync("https://github.com/NaniSoft/nexus", CancellationToken.None);

        Assert.Equal(GitHubClient.MaxIssuePages * GitHubClient.IssuePageSize, issues.Count);
        Assert.Equal(GitHubClient.MaxIssuePages, api.TimesAsked("GET", "/issues?"));
        Assert.Contains("and not all of them", log.Everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repository_that_cannot_be_read_is_refused_before_the_poller_asks_for_anything_else()
    {
        // The shape the poller's own backoff is built on: a refusal that arrives as a
        // refusal, classified, rather than an exception out of a method that has to be
        // inspected to understand.
        using var api = new GitHubApi();
        api.Responding("GET", "/issues?", GitHubApi.Answer.RateLimited("""{"message": "API rate limit exceeded"}"""));

        var refused = await Assert.ThrowsAsync<TransientFailure>(
            () => ClientOver(api, "https://github.com/NaniSoft/nexus")
                .ListOpenIssuesAsync("https://github.com/NaniSoft/nexus", CancellationToken.None));

        Assert.Equal(FailureClass.Transient, Failures.Classify(refused));
        Assert.Equal(1, api.TimesAsked("GET", "/issues?"));
    }

    [Fact]
    public async Task Intake_needs_the_projects_own_credential_and_says_which_one()
    {
        using var api = new GitHubApi();
        var client = ClientOver(
            api,
            "https://github.com/NaniSoft/nexus",
            new FakeCredentialReader());

        var refused = await Assert.ThrowsAsync<PermanentFailure>(
            () => client.ListOpenIssuesAsync("https://github.com/NaniSoft/nexus", CancellationToken.None));

        Assert.Contains("NEXUS_GITHUB_TOKEN", refused.Message, StringComparison.Ordinal);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public void The_branch_is_named_after_the_issue_and_the_pull_request_carries_the_work_items_identity()
    {
        // The two things the spec asks of a pull request, asserted as the request body the
        // client actually sent. Both are about what a human sees in somebody's repository,
        // which is the only place either of them matters.
        var workItem = new WorkItem(
            Guid.NewGuid(),
            "nexus",
            "https://github.com/NaniSoft/nexus",
            42,
            "The poller stops when a project is paced",
            "words",
            "main",
            Swimlane.Review,
            2,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

        var branch = BranchName.For(42, workItem.IssueTitle);

        Assert.Equal("agent-factory/42-the-poller-stops-when-a-project-is-paced", branch);

        // The title is the issue's own, so a reader comparing the two is comparing the same
        // string — and the body's first line is the reference that links the two ways.
        Assert.Equal("The poller stops when a project is paced", PullRequestText.Title(workItem));

        var body = PullRequestText.Body(workItem, branch);
        Assert.StartsWith("Refs #42", body, StringComparison.Ordinal);
        Assert.Contains(branch, body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pull_request_body_can_never_close_the_issue_it_answers()
    {
        // GitHub closes an issue when a pull request body contains a closing keyword next
        // to its number, and merges the pull request for us. That would be a second,
        // unearned claim: whether the factory considers an issue finished is the board's
        // answer about a work item, and this factory can merge a change nobody reviewed —
        // at the 48-hour threshold, or on a retry of a merge that failed. Closing somebody's
        // issue on the strength of that is not what "shipped" means.
        //
        // The list is GitHub's own, and it is written here rather than in the factory,
        // because the factory's job is to avoid all of them and the test's job is to know
        // what all of them are. A new sentence in the body that introduced one would fail
        // this.
        string[] closingKeywords =
        [
            "close", "closes", "closed", "fix", "fixes", "fixed",
            "resolve", "resolves", "resolved",
        ];

        foreach (var title in new[]
        {
            "The poller stops when a project is paced",
            "Fix the thing",
            "Close the loop",
            "Resolved at last",
        })
        {
            var workItem = new WorkItem(
                Guid.NewGuid(), "nexus", "https://github.com/NaniSoft/nexus", 42, title, "words", "main",
                Swimlane.Review, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null);

            var body = PullRequestText.Body(workItem, BranchName.For(42, title));

            foreach (var keyword in closingKeywords)
            {
                Assert.DoesNotContain(
                    $"{keyword} #42",
                    body,
                    StringComparison.OrdinalIgnoreCase);

                Assert.DoesNotContain(
                    $"{keyword}: #42",
                    body,
                    StringComparison.OrdinalIgnoreCase);
            }

            // And the link itself, which is what a reader needs: the issue number, named.
            Assert.Contains("Refs #42", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_branch_name_is_something_git_will_accept_and_a_person_will_recognise()
    {
        foreach (var (title, expected) in new (string?, string)[]
        {
            ("The poller stops when a project is paced", "the-poller-stops-when-a-project-is-paced"),
            ("Fix: the thing  (again!)", "fix-the-thing-again"),
            ("  leading and trailing  ", "leading-and-trailing"),
            ("UPPER Case Title", "upper-case-title"),
            ("a/b\\c:d*e?f[g]h", "a-b-c-d-e-f-g-h"),
            ("", "change"),
            (null, "change"),
            ("!!!", "change"),
            ("修复轮次超时", "change"),
        })
        {
            Assert.Equal(expected, BranchName.Slug(title));
        }

        // Long enough to be recognisable, short enough that a ref stays well inside git's
        // own limits whatever the issue is called.
        Assert.Equal(BranchName.MaxSlugLength, BranchName.Slug(new string('x', 400)).Length);
        Assert.DoesNotContain("..", BranchName.For(1, new string('x', 400)), StringComparison.Ordinal);
    }

    private static string Issues(int count, int first)
    {
        var builder = new StringBuilder("[");

        for (var index = 0; index < count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append($$"""{ "number": {{first + index}}, "title": "Issue {{first + index}}", "body": null }""");
        }

        return builder.Append(']').ToString();
    }
}
