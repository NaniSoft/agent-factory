namespace AgentFactory.Tests.GitHub;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// GitHub's API, faked at the transport. It answers from routes a test wrote and records
/// every request it was actually given — method, path, query, body, headers — so a test can
/// assert the shape of what the factory asked for rather than that some class called some
/// other class.
/// </summary>
/// <remarks>
/// The seam is <see cref="HttpMessageHandler"/> and not the client, so the client, its
/// classification, its git invocations and its own ordering all run for real. Only the
/// network is gone, and the network is not what any of this ticket's arguments are about.
/// </remarks>
public sealed class GitHubApi : HttpMessageHandler
{
    private readonly List<Route> _routes = [];

    /// <summary>Every request the factory made, in order.</summary>
    public List<Recorded> Requests { get; } = [];

    /// <summary>What each request was, as "METHOD /path?query", for order assertions.</summary>
    public List<string> Shapes => [.. Requests.Select(request => request.Shape)];

    /// <summary>Answers every request with this, whatever it is.</summary>
    public GitHubApi Responding(HttpStatusCode status, string body)
    {
        _routes.Add(new Route(null, null, _ => Answer.Of(status, body)));
        return this;
    }

    /// <summary>Answers requests of one method whose path contains a fragment.</summary>
    public GitHubApi Responding(string method, string pathContains, HttpStatusCode status, string body) =>
        Responding(method, pathContains, Answer.Of(status, body));

    /// <summary>Answers requests of one method whose path contains a fragment, the same way every time.</summary>
    public GitHubApi Responding(string method, string pathContains, (HttpStatusCode Status, string Body) answer) =>
        Responding(method, pathContains, Answer.Of(answer.Status, answer.Body));

    /// <summary>
    /// Answers requests of one method whose path contains a fragment, with headers that
    /// matter — which for this API means a rate limit, the one refusal that is about timing
    /// rather than about permission.
    /// </summary>
    public GitHubApi Responding(string method, string pathContains, Answer answer) =>
        Responding(method, pathContains, _ => answer);

    /// <summary>
    /// Answers requests of one method whose path contains a fragment, from the request
    /// itself — which is what a test needs to answer a sequence of calls to the same
    /// endpoint differently.
    /// </summary>
    public GitHubApi Responding(string method, string pathContains, Func<Recorded, Answer> answer)
    {
        // Replacing rather than appending. A test that scripts one endpoint and then
        // re-scripts it — "the first call finds nothing, the second finds the pull request"
        // — is the ordinary shape of the cases here, and an appending fake would keep
        // answering with the earlier script, so a test could pass on a call the faked
        // remote never made. Replacements go last, because the first match still wins.
        _routes.RemoveAll(route => route.Method == method.ToUpperInvariant()
            && string.Equals(route.PathContains, pathContains, StringComparison.Ordinal));

        _routes.Add(new Route(method.ToUpperInvariant(), pathContains, answer));
        return this;
    }

    /// <summary>
    /// A pull request as GitHub writes one. Written out field by field rather than
    /// serialised from the client's own type, because a fixture built from the code under
    /// test would agree with it by construction.
    /// </summary>
    public static string PullRequest(
        int number,
        string headSha,
        string branch,
        string baseRef = "main",
        bool merged = false,
        string? mergeableState = "clean",
        string? state = null) => $$"""
            {
              "number": {{number}},
              "state": "{{state ?? (merged ? "closed" : "open")}}",
              "merged_at": {{(merged ? "\"2026-01-02T03:04:05Z\"" : "null")}},
              "html_url": "https://github.com/NaniSoft/nexus/pull/{{number}}",
              "head": { "ref": "{{branch}}", "sha": "{{headSha}}" },
              "base": { "ref": "{{baseRef}}", "sha": "0000000000000000000000000000000000000000" }
              {{(mergeableState is null ? "" : $", \"mergeable_state\": \"{mergeableState}\"")}}
            }
            """;

    /// <summary>
    /// The list endpoint's answer. An array even when there is one thing in it, because
    /// that is the shape GitHub uses and the client parses — a fixture that returned a bare
    /// object would agree with a client that had been written to expect one.
    /// </summary>
    public static string PullRequestList(params string[] pulls) => $"[{string.Join(',', pulls)}]";

    /// <summary>
    /// The list of pull requests for a head, filtered the way GitHub filters it.
    /// </summary>
    /// <remarks>
    /// The <c>state</c> parameter is honoured rather than ignored, because this is the one
    /// endpoint where the parameter decides an outcome: a client that asked for
    /// <c>state=open</c> would not be shown a pull request that has already been merged, and
    /// would then conclude that nothing had ever been shipped for this branch. A fake that
    /// answered every <c>state</c> the same way would make that mistake invisible — the
    /// client would look correct and the ordering test would have nothing to say about the
    /// query it exists to check.
    /// </remarks>
    public static string PullRequestListFor(string state, params string[] pulls)
    {
        var kept = new List<string>(pulls);

        if (state is "open")
        {
            kept = [.. pulls.Where(pull => pull.Contains("\"state\": \"open\""))];
        }
        else if (state is "closed")
        {
            kept = [.. pulls.Where(pull => pull.Contains("\"state\": \"closed\""))];
        }

        return $"[{string.Join(',', kept)}]";
    }

    /// <summary>The answer to "is this branch on the remote, and at what commit".</summary>
    public static string Ref(string sha) => $$"""{ "ref": "refs/heads/whatever", "object": { "sha": "{{sha}}" } }""";

    /// <summary>How many times a request of this shape was made.</summary>
    public int TimesAsked(string method, string pathContains) => Requests
        .Count(request => request.Method == method.ToUpperInvariant()
            && request.PathAndQuery.Contains(pathContains, StringComparison.Ordinal));

    /// <summary>
    /// The list endpoint's own filtering, so that a test which scripts "there is a merged
    /// pull request on this branch" and a client which asks for <c>state=open</c> disagree
    /// in the way GitHub would make them disagree.
    /// </summary>
    public static string PullRequestsMatching(string pathAndQuery, params string[] pulls)
    {
        var query = pathAndQuery.Contains('?') ? pathAndQuery[(pathAndQuery.IndexOf('?') + 1)..] : string.Empty;
        var state = query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0] == "state")
            .Select(pair => pair[1])
            .FirstOrDefault();

        return PullRequestListFor(state ?? "open", pulls);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        var recorded = new Recorded(
            request.Method.Method.ToUpperInvariant(),
            request.RequestUri!.PathAndQuery,
            body,
            request.Headers.ToDictionary(
                header => header.Key,
                header => string.Join(", ", header.Value),
                StringComparer.OrdinalIgnoreCase),
            request.Headers.Authorization?.ToString() ?? string.Empty);

        Requests.Add(recorded);

        var answer = _routes
            .Where(route => route.Method is null || route.Method == recorded.Method)
            .Where(route => route.PathContains is null
                || recorded.PathAndQuery.Contains(route.PathContains, StringComparison.Ordinal))
            .Select(route => route.Answer(recorded))
            .FirstOrDefault()
            ?? Answer.NotFound();

        var response = new HttpResponseMessage(answer.Status)
        {
            Content = new StringContent(answer.Body, Encoding.UTF8, "application/json"),
        };

        foreach (var header in answer.Headers)
        {
            response.Headers.TryAddWithoutValidation(header.Name, header.Value);
        }

        return response;
    }

    protected override void Dispose(bool disposing) => base.Dispose(disposing);

    private sealed record Route(string? Method, string? PathContains, Func<Recorded, Answer> Answer);

    /// <summary>One request, as it was actually made.</summary>
    public sealed record Recorded(
        string Method,
        string PathAndQuery,
        string Body,
        IReadOnlyDictionary<string, string> Headers,
        string Authorization)
    {
        /// <summary>"METHOD /path?query", which is the shape a test asserts on.</summary>
        public string Shape => $"{Method} {PathAndQuery}";

        /// <summary>The body as JSON, for reading a field out of what was sent.</summary>
        public JsonElement Json() => JsonDocument.Parse(Body).RootElement.Clone();
    }

    /// <summary>What a route answers with: a status, a body, and any headers that matter.</summary>
    public sealed record Answer(HttpStatusCode Status, string Body, IReadOnlyList<(string Name, string Value)> Headers)
    {
        /// <summary>An answer with no headers, which is nearly all of them.</summary>
        public static Answer Of(HttpStatusCode status, string body) => new(status, body, []);

        /// <summary>GitHub's own "there is nothing there", which several endpoints give.</summary>
        public static Answer NotFound() => Of(HttpStatusCode.NotFound, """{"message": "Not Found"}""");

        /// <summary>
        /// A rate limit. A 403 with a number attached rather than a 429, because that is
        /// what GitHub sends for both its primary and its secondary limits, and the header
        /// is the only thing that tells a rate limit from a permission.
        /// </summary>
        public static Answer RateLimited(string body = """{"message": "API rate limit exceeded"}""") => new(
            HttpStatusCode.Forbidden,
            body,
            [("X-RateLimit-Remaining", "0"), ("Retry-After", "60")]);
    }
}

/// <summary>
/// A logger that keeps every line it was given, formatted as a log sink would format it.
/// </summary>
/// <remarks>
/// The one thing this exists for is a claim that cannot be made by reading code: that the
/// factory's own logging never carries a credential. A test that greps the source proves
/// only that the source does not say the word; this is what a credential would come out of,
/// formatted by <see cref="ILogger"/> exactly as a log file would format it, including the
/// message of an exception the factory is about to hand a reviewer on the board.
/// </remarks>
public sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines => _lines;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // The formatted line, with the exception's own message folded in, because that is
        // what a log sink writes and therefore what a credential would have to hide from.
        var line = formatter(state, exception);
        if (exception is not null)
        {
            line = $"{line} {exception.Message} {exception.InnerException?.Message}";
        }

        _lines.Add(line);
    }

    /// <summary>Everything that was logged, as one blob, for a "this string is not in there" check.</summary>
    public string Everything => string.Join('\n', _lines);
}
