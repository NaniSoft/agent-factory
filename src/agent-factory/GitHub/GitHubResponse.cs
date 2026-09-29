namespace AgentFactory.GitHub;

using System.Net;
using System.Net.Http.Headers;
using AgentFactory.Failures;

/// <summary>
/// The merger's whole judgement about a GitHub answer, in one table. This is where #7's
/// "the merger must declare its failures" is discharged: the classification is made here,
/// from the status code and two headers, at the point the answer was read — never from a
/// word in a message, and never by a caller above.
/// </summary>
/// <remarks>
/// <para>
/// The reason a status code rather than a message is the whole of the design. GitHub says
/// a great deal through prose — "Must have push access to view repository diffs" is a
/// 404 that is really a permission problem, and "Base branch was modified" is a 405 that
/// is really a race — and a factory that reads its answers for substrings would be
/// guessing in exactly the way the rest of this codebase refuses to guess. Where prose is
/// the only signal, this class says so rather than pretending: see
/// <see cref="Unclassifiable"/>.
/// </para>
/// <para>
/// The table, and why each row is where it is:
/// </para>
/// <list type="table">
/// <item><term>401</term><description>Transient? No. The token is wrong or revoked, and a
/// second attempt with it will be refused the same way. Permanent.</description></item>
/// <item><term>403, 429 with a rate-limit header</term><description>Transient. GitHub is
/// saying how long to wait, and it is the only GitHub answer that comes with a number
/// attached. <c>Retry-After</c> or <c>X-RateLimit-Remaining: 0</c> — either says the same
/// thing, and a secondary rate limit is announced the same way.</description></item>
/// <item><term>403 otherwise</term><description>Permanent. Insufficient scope, SSO not
/// satisfied, the branch is protected, an org will not let a token do this. All of them
/// are answers about this token and this repository, and none of them changes in ten
/// seconds.</description></item>
/// <item><term>404</term><description>Permanent. There is no such repository, or this
/// token cannot see it. Note what it is not asked to mean: GitHub returns 404 rather than
/// 403 for a repository a token may not read, so this also covers "you cannot see this",
/// and both are permanent.</description></item>
/// <item><term>409, 422</term><description>Permanent. GitHub has refused the request
/// itself — a conflict, or a body it will not accept. Nothing about repeating the identical
/// request changes the answer.</description></item>
/// <item><term>5xx</term><description>Transient. GitHub is having a bad time, which is the
/// definition of the class.</description></item>
/// </list>
/// </remarks>
public static class GitHubResponse
{
    /// <summary>How much of an error body is worth carrying into a log line and a board.</summary>
    private const int BodyCharacters = 300;

    /// <summary>
    /// The refusal an answer is, as the kind of failure it is. The body is carried because
    /// it is GitHub's own account of what it objected to and a reviewer reading the board
    /// is owed it — not because anything here reads it.
    /// </summary>
    /// <param name="what">What the factory was asking for, in its own words.</param>
    public static Exception Refusal(
        HttpStatusCode status,
        HttpResponseHeaders? headers,
        string? body,
        string what)
    {
        var said = Shorten(body);
        var because = said.Length > 0 ? $": {said}" : ".";

        return status switch
        {
            HttpStatusCode.Unauthorized => new PermanentFailure(
                $"GitHub refused {what} with 401: the token this project names was not accepted{because}"),

            HttpStatusCode.NotFound => new PermanentFailure(
                $"GitHub refused {what} with 404: there is no such repository, no such issue, or no such "
                    + $"branch for this token to see{because}"),

            HttpStatusCode.Conflict => new PermanentFailure(
                $"GitHub refused {what} with 409: the request conflicts with the repository's own state, "
                    + $"and repeating it exactly would be refused the same way{because}"),

            (HttpStatusCode)422 => new PermanentFailure(
                $"GitHub refused {what} with 422: the request was not one it would accept{because}"),

            HttpStatusCode.TooManyRequests => new TransientFailure(
                $"GitHub is rate-limiting this factory and asked it to wait before asking {what} again"),

            _ => (int)status >= 500
                ? new TransientFailure(
                    $"GitHub answered {what} with {(int)status}, which is GitHub's side rather than this "
                        + $"factory's{because}")
                : IsRateLimited(headers)
                    ? new TransientFailure(
                        $"GitHub is rate-limiting this factory and asked it to wait before asking {what} again")
                    : new PermanentFailure(
                        $"GitHub refused {what} with {(int)status}{because}"),
        };
    }

    /// <summary>
    /// Whether this answer is GitHub saying "not now" rather than "no". Read from the two
    /// headers GitHub sets when it means it, and from nothing else.
    /// </summary>
    public static bool IsRateLimited(HttpResponseHeaders? headers)
    {
        if (headers is null)
        {
            return false;
        }

        if (headers.Contains("Retry-After"))
        {
            return true;
        }

        // A primary rate limit announces itself by spending the quota; a secondary one by
        // the same header, because there is no other number to give.
        return headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            && remaining.Any(value => string.Equals(value, "0", StringComparison.Ordinal));
    }

    /// <summary>
    /// A failure that is not an answer at all: the request never got one. Transient, and
    /// for the reason the Docker CLI's are — the attempt failed to happen rather than
    /// happening and failing. A name lookup, a refused connection, a dropped socket and a
    /// client-side timeout are all the same fact.
    /// </summary>
    /// <param name="what">What the factory was asking for, in its own words.</param>
    public static Exception Transport(Exception failed, string what) => new TransientFailure(
        $"GitHub could not be reached to {what}: {failed.Message}",
        failed);

    /// <summary>
    /// The cases this table cannot decide, named rather than guessed at. Each is a real
    /// thing GitHub does, and each is refused as permanent because the safe reading of "we
    /// do not know" is one attempt and a human.
    /// </summary>
    public static IReadOnlyList<string> Unclassifiable =>
    [
        // 405 on a merge. GitHub uses one status for "the pull request conflicts" (a
        // permanent fact about the change) and for "the base branch moved while you were
        // looking" (a race that a retry would win). The merger's caller does not read the
        // message: it re-reads the pull request and looks at `mergeable_state`, which is
        // GitHub's own structured answer, and a 405 that arrives when that state says
        // `clean` is refused rather than retried.
        "405 on a merge, when the pull request's own mergeable state does not say why",

        // A push git refused. git has no status code, so this is decided by asking the
        // remote instead: whether the branch is now at the commit that was pushed (it
        // landed and git did not say so), and whether the remote can be reached at all. If
        // it can and the branch is not there, git's refusal is the remote's answer and the
        // factory will not repeat it. That is a state, not a guess — but it is also a
        // question this table cannot answer, and it is a question, so it is named.
        "A push that git refused, decided by the remote's state rather than by git's words",

        // A 200 from a merge with a body that does not say it merged. Not expected, and
        // not something a status code can catch. Treated as a merge that landed.
        "A merge that answers 200 with a body that does not say it merged",

        // An answer that arrived and was not the shape the API documents — an HTML error
        // page with a 200, or a body this client cannot parse. There is no status code to
        // read and no state to ask, so it is permanent: repeating an identical request
        // would come back with the same unreadable body.
        "A successful answer whose body is not the shape the API documents",
    ];

    private static string Shorten(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        var one = body.ReplaceLineEndings(" ").Trim();
        return one.Length <= BodyCharacters ? one : one[..BodyCharacters] + "…";
    }
}
