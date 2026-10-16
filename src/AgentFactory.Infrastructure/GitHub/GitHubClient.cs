namespace AgentFactory.Infrastructure.GitHub;

using System.Net.Http.Json;
using System.Net;
using System.Text.Json;

using AgentFactory.Application.Credentials;
using AgentFactory.Domain.Failures;
using AgentFactory.Application.Observability;
using AgentFactory.Application.Projects;
using AgentFactory.Application;
using AgentFactory.Application.GitHub;
using AgentFactory.Domain.Issues;
using AgentFactory.Domain.Projects;
using AgentFactory.Domain.WorkItems;
using Microsoft.Extensions.Logging;

/// <summary>
/// GitHub, over its real API, from the host. Intake reads through it and the merger's
/// adapter is the same class, which is the whole reason it is one class behind two
/// seams: a factory that polled one repository's issues from one place and pushed to
/// another could disagree with itself about what a repository is.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It holds no work item store, and that is the property
/// <c>docs/architecture.md</c> asks for: an adapter never queries the store.</strong>
/// It used to, to find the round's tree, which was legal and was also the deepest coupling
/// in the codebase because two components write the store and one of them was a REST
/// client. The merge policy now resolves the work item, its tree and its commit into a
/// <see cref="MergePlan"/> and hands it over; everything below reads the plan and asks
/// this process's own configuration and the network for the rest.
/// </para>
/// <para>
/// <strong>What belongs to the policy and what belongs here, and why.</strong> The branch
/// a change is called, the title and body of its pull request, the order of the seven
/// steps and every refusal about the factory's own work — already merged, closed without
/// merging, a branch that moved on, a tree holding changes nobody committed — are
/// <see cref="MergePolicy"/>'s. What is here is the two facts only the outside can
/// answer: what GitHub said, and what git said. Classification from a status code and two
/// headers is <see cref="GitHubResponse"/>, and it is here because a status code is a
/// thing GitHub sends.
/// </para>
/// <para>
/// <strong>Failures are declared here</strong>, from status codes and headers, at the point
/// the answer was read. Nothing above this class reads a message to decide anything, and
/// this class does not either: where GitHub's only signal is prose, the caller asks GitHub
/// a second question instead, and the cases that remain are named in
/// <see cref="GitHubResponse.Unclassifiable"/> rather than guessed at.
/// </para>
/// <para>
/// <strong>Every request carries a <c>User-Agent</c>, and that is not politeness.</strong>
/// GitHub <em>refuses</em> a request that does not carry one — a 403 whose body says so —
/// so a client without it cannot read a repository, cannot open a pull request and cannot
/// merge anything. That is not a hypothetical: this client had no <c>User-Agent</c> for its
/// whole first life, every call it made was refused, and the entire external surface of the
/// factory — intake and the merger both — had never worked. It is set on each request in
/// <see cref="AskAsync"/>, which is the one place a request is built, so "every request
/// carries one" is a property of the code path rather than a configuration somebody can
/// drop; and a faked transport cannot catch a header the real service requires, so the test
/// that holds this reads the header off the request the transport was actually handed.
/// </para>
/// <para>
/// <strong>The credential.</strong> Read here, on the host, and used in exactly two places:
/// an <c>Authorization</c> header on each request, and the environment of the one git child
/// that pushes. It is never put in a URL, never put on a command line, and never logged —
/// not in a message, not in a log line, and not in the body of the failure a reviewer reads
/// on the board. The project's own name for it is logged, because a name is not a secret
/// and it is the thing an operator has to go and fix. No worker container is involved in
/// any of this: the container that wrote the change is long gone, and this is the component
/// that ships it.
/// </para>
/// </remarks>
public sealed class GitHubClient : IGitHub, IMerger
{
    /// <summary>
    /// The name of the configured client. Public because the composition root is what
    /// configures it, and a test that substitutes the transport has to name the same one.
    /// </summary>
    public const string HttpClientName = "github";

    /// <summary>
    /// The API version every request declares. GitHub pins behaviour to it, and a client
    /// that does not say which version it was written against is a client that can be
    /// changed underneath itself.
    /// </summary>
    public const string ApiVersion = "2022-11-28";

    /// <summary>
    /// What this factory calls itself to GitHub, on every request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>GitHub refuses every request that does not carry a <c>User-Agent</c></strong>
    /// — a 403 with a body that says so — so this is a requirement of the service rather
    /// than good manners, and a client that omits it has no external surface at all: no
    /// intake and no merge, with every request refused and the refusal looking like any
    /// other 403. That is how this factory's first run against the real API went, and it
    /// is why the header is set on the request in <see cref="AskAsync"/> rather than left to
    /// a configured client's defaults: one place builds a request, so one place can be held
    /// to putting it there.
    /// </para>
    /// <para>
    /// The next person to add a client in this process should read that as the rule rather
    /// than the exception. A faked transport cannot catch a header the real service
    /// requires — it will happily answer a request GitHub would refuse — so the check that
    /// matters reads the header off the request the transport was handed, and a fake that
    /// reproduces one thing the real service refuses is worth more than a test that
    /// asserts the client was configured correctly.
    /// </para>
    /// </remarks>
    public const string UserAgent = "agent-factory";

    /// <summary>How many issues one page of the list asks for — GitHub's own maximum.</summary>
    public const int IssuePageSize = 100;

    /// <summary>
    /// How many pages of open issues one pass will read, so a repository with more open
    /// issues than this cannot make a poll unbounded. Hitting the ceiling is said out loud
    /// rather than passed off as the whole list.
    /// </summary>
    public const int MaxIssuePages = 10;

    /// <summary>
    /// The bound on one GitHub request, and the one clock in this component.
    /// </summary>
    /// <remarks>
    /// It belongs here and not in the loop because the loop is not allowed to have one: the
    /// poller and the orchestrator both hand this seam <c>CancellationToken.None</c> and
    /// leave the bounding to the client that talks to the network, which is exactly what
    /// their own comments say it should be. It is the client's own timeout rather than a
    /// <c>Task.Delay</c> or a timer, so <c>PolicyTests</c>'s one-wait rule is untouched and
    /// the seam has not become a second place the policy lives.
    /// </remarks>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How the change is merged. A merge commit, and not a squash or a rebase: the spec
    /// asks for every merge to be a real pull request that can be reverted, and a merge
    /// commit is the shape where reverting is one action against one commit — and it keeps
    /// the round's own commit intact on the base branch, which is the same commit the
    /// reviewer judged.
    /// </summary>
    public const string MergeMethod = "merge";

    private const string Accept = "application/vnd.github+json";

    private readonly HttpClient _http;
    private readonly ProjectLoadReport _projects;
    private readonly ICredentialReader _credentials;
    private readonly ILogger<GitHubClient> _logger;

    /// <summary>
    /// There is no store here, and no work item either: a merge arrives as a
    /// <see cref="MergePlan"/> and this component reads nothing but the plan, the project's
    /// own configuration and GitHub's answers.
    /// </summary>
    public GitHubClient(
        HttpClient http,
        ProjectLoadReport projects,
        ICredentialReader credentials,
        ILogger<GitHubClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<string> GetDefaultBranchAsync(string repoUrl, CancellationToken cancellationToken)
    {
        var repository = RepositoryRef.Parse(repoUrl);
        var token = TokenFor(ProjectServing(repository));
        var answer = await AskAsync(
            HttpMethod.Get,
            repository.ApiPath,
            token,
            json: null,
            cancellationToken).ConfigureAwait(false);

        Expect(answer, $"the default branch of {repository.FullName}");

        var branch = Read(answer).GetProperty("default_branch").GetString();
        if (string.IsNullOrWhiteSpace(branch))
        {
            throw new PermanentFailure(
                $"GitHub named no default branch for {repository.FullName}, so there is nothing for a round to be "
                    + "built against");
        }

        return branch;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OpenIssue>> ListOpenIssuesAsync(
        string repoUrl,
        CancellationToken cancellationToken)
    {
        var repository = RepositoryRef.Parse(repoUrl);
        var token = TokenFor(ProjectServing(repository));
        var issues = new List<OpenIssue>();
        var truncated = false;

        for (var page = 1; page <= MaxIssuePages; page++)
        {
            var answer = await AskAsync(
                HttpMethod.Get,
                $"{repository.ApiPath}/issues?state=open&per_page={IssuePageSize}&page={page}",
                token,
                json: null,
                cancellationToken).ConfigureAwait(false);

            Expect(answer, $"the open issues of {repository.FullName}");

            var page_ = Read(answer).EnumerateArray().ToList();
            foreach (var element in page_)
            {
                // The issues endpoint returns pull requests too, and a pull request is not
                // an issue the factory should build from: it would intake its own output,
                // and the work item it made would be one the board then tried to ship a
                // second time. GitHub marks each one with the pull request it is.
                if (element.TryGetProperty("pull_request", out _))
                {
                    continue;
                }

                issues.Add(new OpenIssue(
                    element.GetProperty("number").GetInt32(),
                    element.GetProperty("title").GetString() ?? string.Empty,
                    element.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
                    [.. element.TryGetProperty("labels", out var labels)
                        ? labels.EnumerateArray().Select(label => label.GetProperty("name").GetString() ?? string.Empty)
                        : []],
                    [.. element.TryGetProperty("assignees", out var assignees)
                        ? assignees.EnumerateArray().Select(who => who.GetProperty("login").GetString() ?? string.Empty)
                        : []]));
            }

            if (page_.Count < IssuePageSize)
            {
                break;
            }

            if (page == MaxIssuePages)
            {
                truncated = true;
            }
        }

        if (truncated)
        {
            // Said rather than passed off as a whole list. An intake that quietly read the
            // first thousand open issues of a repository with more would look exactly like
            // a repository that had only a thousand.
            _logger.LogWarning(
                "{Repository} still had open issues on page {Pages} of the open-issue list, so this pass read "
                    + "the first {Read} and not all of them.",
                repository.FullName,
                MaxIssuePages,
                MaxIssuePages * IssuePageSize);
        }

        return issues;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IssueComment>> ReadCommentsAsync(
        string repoUrl,
        int issueNumber,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(issueNumber);

        var repository = RepositoryRef.Parse(repoUrl);
        var token = TokenFor(ProjectServing(repository));
        var comments = new List<IssueComment>();
        var truncated = false;

        for (var page = 1; page <= MaxIssuePages; page++)
        {
            var answer = await AskAsync(
                HttpMethod.Get,
                $"{repository.ApiPath}/issues/{issueNumber}/comments?per_page={IssuePageSize}&page={page}",
                token,
                json: null,
                cancellationToken).ConfigureAwait(false);

            Expect(answer, $"the conversation on {repository.FullName}#{issueNumber}");

            var page_ = Read(answer).EnumerateArray().ToList();
            foreach (var element in page_)
            {
                comments.Add(new IssueComment(
                    element.TryGetProperty("user", out var user)
                        ? user.GetProperty("login").GetString() ?? string.Empty
                        : string.Empty,
                    element.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
                    element.TryGetProperty("created_at", out var createdAt)
                        && DateTimeOffset.TryParse(createdAt.GetString(), out var when)
                            ? when
                            : DateTimeOffset.MinValue));
            }

            if (page_.Count < IssuePageSize)
            {
                break;
            }

            if (page == MaxIssuePages)
            {
                truncated = true;
            }
        }

        if (truncated)
        {
            // The same honesty the issue list carries: a conversation longer than the bound
            // is a conversation the factory saw the start of, and a round brief built on it
            // would read like the whole story when it was only the opening.
            _logger.LogWarning(
                "The conversation on {Repository}#{Issue} ran past page {Pages}, so this read took the "
                    + "first {Read} comments and not all of them.",
                repository.FullName,
                issueNumber,
                MaxIssuePages,
                MaxIssuePages * IssuePageSize);
        }

        return comments;
    }

    /// <inheritdoc />
    public async Task<PullRequest> OpenPullRequestAsync(
        PullRequestRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var repository = RepositoryRef.Parse(request.RepoUrl);
        var token = TokenFor(ProjectServing(repository));

        // The base branch is on the request rather than read from the store, and that is the
        // last of the store coupling gone: this used to look the work item up to find out
        // which branch the round was built against, in a REST client, because the one
        // caller of this had a work item and did not put it on the request. The caller
        // still has it — it is the work item's own base branch, from intake.
        var opened = await PostThePullRequestAsync(
            repository,
            request.Branch,
            request.Title,
            request.Body,
            request.BaseBranch,
            request.IssueNumber,
            token,
            cancellationToken).ConfigureAwait(false);

        return new PullRequest(opened.Number, opened.Url);
    }

    /// <inheritdoc />
    public async Task<CreatedIssue> CreateIssueAsync(NewIssue request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);

        var repository = RepositoryRef.Parse(request.RepoUrl);
        var token = TokenFor(ProjectServing(repository));

        // The parent is resolved before the create rather than being handed to the endpoint
        // as a number, for two reasons and the first is the load-bearing one: a create that
        // succeeded and then failed to be linked would leave an orphan issue in a human's
        // tracker with a title that says it is part of something. Resolving first means the
        // only way this can happen is a refusal before anything is created at all — a parent
        // this token cannot see fails here rather than after an orphan exists. The resolved
        // id itself is discarded: the link endpoint addresses the parent by number and the
        // child by the id the create answers with.
        if (request.ParentNumber is { } parentNumber)
        {
            _ = await IssueIdAsync(repository, parentNumber, token, cancellationToken)
                .ConfigureAwait(false);
        }

        var answer = await AskAsync(
            HttpMethod.Post,
            $"{repository.ApiPath}/issues",
            token,
            json: new Dictionary<string, object?>
            {
                ["title"] = request.Title,
                ["body"] = request.Body,

                // Labels are applied for humans reading the tracker and for the tools around
                // it. They are deliberately not read back by the factory — the work item's
                // kind is a column the factory writes, so a label here
                // is presentation and not an input, and nothing this process does changes if
                // somebody edits or removes it.
                ["labels"] = new[] { IGitHub.FactoryIssueLabel },
            },
            cancellationToken).ConfigureAwait(false);

        Expect(answer, $"an issue on {repository.FullName}");

        var created = Read(answer);
        var number = created.GetProperty("number").GetInt32();
        var url = created.TryGetProperty("html_url", out var html) ? html.GetString() ?? string.Empty : string.Empty;

        // Second pass, and deliberately after the create rather than instead of it. The
        // issue has to exist before it can be made a child of anything, so "create and link
        // in one request" is not available; what is available is doing the link immediately
        // after and refusing the whole thing if it fails, so a caller never sees a created
        // issue it was not told about. The endpoint takes the parent's number in the path
        // and the child's database id in the body, which is what the create just answered.
        if (request.ParentNumber is { } parent)
        {
            var createdId = created.GetProperty("id").GetInt64();
            await LinkAsSubIssueAsync(repository, parent, createdId, token, cancellationToken)
                .ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Opened {Repository}#{Created} as a child of #{Parent}: {Title}",
            repository.FullName,
            number,
            request.ParentNumber,
            request.Title);

        return new CreatedIssue(number, url);
    }

    /// <inheritdoc />
    public async Task MakeASubIssueAsync(
        string repoUrl,
        int parentNumber,
        int childNumber,
        CancellationToken cancellationToken)
    {
        var repository = RepositoryRef.Parse(repoUrl);
        var token = TokenFor(ProjectServing(repository));

        // The path addresses the parent by number; the body carries the child's database
        // id, which only has to be resolved, never guessed from a number.
        var child = await IssueIdAsync(repository, childNumber, token, cancellationToken)
            .ConfigureAwait(false);

        await LinkAsSubIssueAsync(repository, parentNumber, child, token, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MakeBlockedByAsync(
        string repoUrl,
        int blockedNumber,
        int blockerNumber,
        CancellationToken cancellationToken)
    {
        var repository = RepositoryRef.Parse(repoUrl);
        var token = TokenFor(ProjectServing(repository));

        // GitHub's own dependency relationship rather than a line in a body. The reason is
        // that a blocking edge written in prose is invisible in the one view where somebody
        // asks "what can I work on", and this factory's whole claim about its maps is that a
        // human can read the frontier without opening the factory. The path addresses the
        // blocked issue by number; the body carries the blocker's database id, which is
        // resolved here rather than assumed to be the number. The blocked issue is resolved
        // for the same reason the parent above is: a number this token cannot see is a
        // refusal before any write, not a 404 after one.
        _ = await IssueIdAsync(repository, blockedNumber, token, cancellationToken)
            .ConfigureAwait(false);
        var blocker = await IssueIdAsync(repository, blockerNumber, token, cancellationToken)
            .ConfigureAwait(false);

        var answer = await AskAsync(
            HttpMethod.Post,
            $"{repository.ApiPath}/issues/{blockedNumber}/dependencies/blocked_by",
            token,
            json: new Dictionary<string, object?> { ["issue_id"] = blocker },
            cancellationToken).ConfigureAwait(false);

        Expect(answer, $"a blocking edge on {repository.FullName}#{blockedNumber}");
    }

    /// <inheritdoc />
    public async Task CloseIssueAsync(
        string repoUrl,
        int issueNumber,
        string because,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(because);

        var repository = RepositoryRef.Parse(repoUrl);
        var token = TokenFor(ProjectServing(repository));

        // A comment and then a close, rather than a close. GitHub's `state_reason` field can
        // carry a reason, but a reason that only exists on the close event is a reason a
        // reader has to know to go and look for; a comment puts it in the issue's own
        // conversation, in order, next to the children that replaced it.
        var comment = await AskAsync(
            HttpMethod.Post,
            $"{repository.ApiPath}/issues/{issueNumber}/comments",
            token,
            json: new Dictionary<string, object?> { ["body"] = because },
            cancellationToken).ConfigureAwait(false);

        Expect(comment, $"a closing comment on {repository.FullName}#{issueNumber}");

        var closed = await AskAsync(
            HttpMethod.Patch,
            $"{repository.ApiPath}/issues/{issueNumber}",
            token,
            json: new Dictionary<string, object?>
            {
                ["state"] = "closed",

                // Not `completed`: this issue was not delivered, it was superseded by its
                // own children. GitHub's two words for "done" mean different things here
                // and saying the wrong one would put a completed-looking label on an issue
                // whose work has not been done.
                ["state_reason"] = "not_planned",
            },
            cancellationToken).ConfigureAwait(false);

        Expect(closed, $"closing {repository.FullName}#{issueNumber}");
    }

    /// <inheritdoc />
    public Task ResolveAsync(MergePlan plan, CancellationToken cancellationToken)
    {
        var repository = RepositoryFor(plan);

        // Both refusals are raised here rather than at the first request, so that a
        // misconfigured project and an absent credential are answered with nothing sent and
        // nothing started. The order is the policy's to decide; the facts are this class's
        // to answer.
        _ = TokenFor(ProjectServing(repository));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<TreeAtHead> ReadTheTreeAsync(string tree, CancellationToken cancellationToken)
    {
        // The uncommitted read comes second and is asked whatever the first said, because
        // "there is no commit" and "there is no commit *and* something uncommitted" are
        // different refusals with different messages and the policy needs both facts to
        // tell them apart. Nothing here decides: a null is a null.
        var head = await GitPusher
            .RunAsync(tree, GitPusher.HeadArguments(), authorization: null, cancellationToken)
            .ConfigureAwait(false);

        var uncommitted = await GitPusher
            .RunAsync(tree, GitPusher.UncommittedArguments(), authorization: null, cancellationToken)
            .ConfigureAwait(false);

        return new TreeAtHead(
            head.Succeeded ? head.Output.Trim() : null,
            uncommitted.Succeeded ? uncommitted.Output : null);
    }

    /// <inheritdoc />
    public async Task<PullRequestOnRemote?> FindThePullRequestAsync(
        MergePlan plan,
        CancellationToken cancellationToken)
    {
        var repository = RepositoryFor(plan);
        var token = TokenFor(ProjectServing(repository));
        var branch = plan.Branch;

        // `state=all`, because "no pull request" has to mean no pull request rather than no
        // <em>open</em> one: a retry after a merge that GitHub accepted but this process
        // never saw the answer to would otherwise find nothing, push again, and open a
        // second pull request for a change that is already on the base branch. When several
        // come back the highest number wins, which is the most recent one — the only one that
        // could be the one this process is looking for.
        var head = Uri.EscapeDataString($"{repository.Owner}:{branch}");
        var answer = await AskAsync(
            HttpMethod.Get,
            $"{repository.ApiPath}/pulls?state=all&per_page=100&head={head}",
            token,
            json: null,
            cancellationToken).ConfigureAwait(false);

        Expect(answer, $"the pull requests of {repository.FullName}");

        return Read(answer)
            .EnumerateArray()
            .Select(PullFrom)
            .OrderByDescending(pull => pull.Number)
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<string?> BranchOnTheRemoteAsync(MergePlan plan, CancellationToken cancellationToken)
    {
        var repository = RepositoryFor(plan);
        var token = TokenFor(ProjectServing(repository));
        var branch = plan.Branch;

        // The ref is addressed by its full path under refs/, so the branch's own slash is
        // part of the path rather than something to escape.
        var answer = await AskAsync(
            HttpMethod.Get,
            $"{repository.ApiPath}/git/ref/heads/{branch}",
            token,
            json: null,
            cancellationToken).ConfigureAwait(false);

        if (answer.Status == HttpStatusCode.NotFound)
        {
            return null;
        }

        Expect(answer, $"the branch {branch} of {repository.FullName}");

        return Read(answer).GetProperty("object").GetProperty("sha").GetString();
    }

    /// <inheritdoc />
    public async Task<bool> TheRemoteIsReachableAsync(MergePlan plan, CancellationToken cancellationToken)
    {
        var repository = RepositoryFor(plan);
        var token = TokenFor(ProjectServing(repository));

        try
        {
            // Through `Expect`, so a refusal is raised and classified here rather than
            // being counted as a success: a 404 for a repository this token cannot see is
            // an answer, and reading it as "reachable" would turn a permission problem into
            // a permanent push failure with the wrong explanation.
            var answer = await AskAsync(
                HttpMethod.Get,
                repository.ApiPath,
                token,
                json: null,
                cancellationToken).ConfigureAwait(false);

            Expect(answer, $"the repository {repository.FullName}");

            return true;
        }
        catch (TransientFailure unreachable)
        {
            _logger.LogWarning(unreachable, "{Repository} could not be reached: {Reason}", repository.FullName, unreachable.Message);
            return false;
        }
        catch (PermanentFailure refused)
        {
            _logger.LogWarning(refused, "{Repository} answered and refused: {Reason}", repository.FullName, refused.Message);
            return true;
        }
    }

    /// <inheritdoc />
    public async Task<PushAttempt> PushTheCommitAsync(MergePlan plan, CancellationToken cancellationToken)
    {
        var repository = RepositoryFor(plan);
        var token = TokenFor(ProjectServing(repository));

        var pushed = await GitPusher.PushAsync(
            plan.Tree,
            repository.PushUrl,
            plan.Commit,
            plan.Branch,
            GitPusher.BasicAuthorization(token),
            cancellationToken).ConfigureAwait(false);

        // The exit code is reported and not interpreted. What a non-zero exit means is a
        // question about the remote, and only the policy can turn it into an answer — by
        // asking the remote, twice, rather than by reading git's words.
        return new PushAttempt(pushed.Succeeded, pushed.Summary);
    }

    /// <inheritdoc />
    public async Task<PullRequestOnRemote> OpenThePullRequestAsync(
        MergePlan plan,
        CancellationToken cancellationToken)
    {
        var repository = RepositoryFor(plan);
        var token = TokenFor(ProjectServing(repository));

        var answer = await AskAsync(
            HttpMethod.Post,
            $"{repository.ApiPath}/pulls",
            token,
            json: new Dictionary<string, object?>
            {
                ["title"] = plan.Title,
                ["body"] = plan.Body,
                ["head"] = plan.Branch,

                // GitHub's own name for the branch the change lands on. The work item's base
                // is where the round was built, and the two are the same by construction:
                // intake resolved the default branch and the round fetched it.
                ["base"] = plan.WorkItem.BaseBranch,

                // The factory does not maintain branches, so it does not ask for the
                // permission to. Said here rather than left to the default because the
                // default is a question about somebody else's repository, and this is the
                // one place in the factory that gets to have an opinion about it.
                ["maintainer_can_modify"] = false,
                ["draft"] = false,
            },
            cancellationToken).ConfigureAwait(false);

        if ((int)answer.Status == 422)
        {
            // GitHub refuses a second pull request for the same head and base. That is the
            // race the read-before-write step cannot close on its own — two factories, or a
            // factory and a human, arriving at once — and it is closed by asking again
            // rather than by reading the 422's message: if a pull request is there now, it
            // is the one to merge, and if there is not, the 422 was about something else
            // and is a real refusal.
            var raced = await FindThePullRequestAsync(plan, cancellationToken).ConfigureAwait(false);

            if (raced is not null)
            {
                _logger.LogWarning(
                    "GitHub refused to open a second pull request for {Branch} on {Repository}, and there is one "
                        + "there now (#{Pull}), so this attempt will finish that one instead.",
                    plan.Branch,
                    repository.FullName,
                    raced.Number);

                return raced;
            }
        }

        Expect(answer, $"a pull request for {plan.Branch} on {repository.FullName}");

        var opened = PullFrom(Read(answer));
        _logger.LogInformation(
            "Opened {Repository}#{Pull} from {Branch} onto {Base}, answering issue #{Issue}.",
            repository.FullName,
            opened.Number,
            plan.Branch,
            plan.WorkItem.BaseBranch,
            plan.IssueNumber);

        return opened;
    }

    /// <inheritdoc />
    public async Task<PullRequestOnRemote?> ReadThePullRequestAsync(
        MergePlan plan,
        int number,
        CancellationToken cancellationToken)
    {
        var repository = RepositoryFor(plan);
        var token = TokenFor(ProjectServing(repository));

        var answer = await AskAsync(
            HttpMethod.Get,
            $"{repository.ApiPath}/pulls/{number}",
            token,
            json: null,
            cancellationToken).ConfigureAwait(false);

        // A read that fails is not a reason to change the answer the caller already has.
        // This read is only there to say whether the merge landed despite it, so it reports
        // what it found and says nothing when it found nothing.
        if (!answer.Ok)
        {
            _logger.LogWarning(
                "Could not read {Repository}#{Pull} back after a merge that did not report one: {Reason}",
                repository.FullName,
                number,
                GitHubResponse.Refusal(answer.Status, answer.Headers, answer.Body, "reading a pull request back").Message);

            return null;
        }

        return PullFrom(Read(answer));
    }

    /// <inheritdoc />
    public async Task MergeThePullRequestAsync(
        MergePlan plan,
        PullRequestOnRemote pull,
        CancellationToken cancellationToken)
    {
        var repository = RepositoryFor(plan);
        var token = TokenFor(ProjectServing(repository));

        var answer = await AskAsync(
            new HttpMethod("PUT"),
            $"{repository.ApiPath}/pulls/{pull.Number}/merge",
            token,
            json: new Dictionary<string, object?> { ["merge_method"] = MergeMethod },
            cancellationToken).ConfigureAwait(false);

        if (answer.Ok && Read(answer).TryGetProperty("merged", out var merged) && merged.GetBoolean())
        {
            return;
        }

        // Either GitHub refused, or it answered 200 and said in the body that it did not
        // merge. Both are answered the same way and neither is answered by reading the
        // answer: what the pull request is *now* is GitHub's own structured account of
        // whether the change landed, and that is the question a retry turns on.
        var now = await ReadThePullRequestAsync(plan, pull.Number, cancellationToken).ConfigureAwait(false);

        if (now is { Merged: true })
        {
            _logger.LogWarning(
                "The merge of {Repository}#{Pull} did not report a merge ({Reason}), but the pull request is merged, "
                    + "so it landed and this is treated as a merge that worked.",
                repository.FullName,
                pull.Number,
                answer.Summary);

            return;
        }

        if (answer.Ok)
        {
            throw new PermanentFailure(
                $"GitHub answered a merge of {repository.FullName}#{pull.Number} with {(int)answer.Status} and said it "
                    + "did not merge"
                    + (string.IsNullOrWhiteSpace(answer.Summary) ? "." : $": {answer.Summary}")
                    + " Nothing about asking again would change that");
        }

        throw WhyTheMergeWasRefused(repository, pull, answer, now);
    }

    /// <inheritdoc />
    public async Task<bool> IsTheIssueOpenAsync(MergePlan plan, int issueNumber, CancellationToken cancellationToken)
    {
        var repository = RepositoryFor(plan);
        var token = TokenFor(ProjectServing(repository));

        var answer = await AskAsync(
            HttpMethod.Get,
            $"{repository.ApiPath}/issues/{issueNumber}",
            token,
            json: null,
            cancellationToken).ConfigureAwait(false);

        Expect(answer, $"an issue on {repository.FullName}");

        return !(Read(answer).TryGetProperty("state", out var state)
            && string.Equals(state.GetString(), "closed", StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public Task CloseTheIssueAsync(
        MergePlan plan,
        int issueNumber,
        string because,
        CancellationToken cancellationToken) =>
        CloseIssueAsync(plan.Repository, issueNumber, because, cancellationToken);

    /// <summary>
    /// The refusal a merge is, decided from the pull request's own mergeable state where
    /// GitHub gave one status for two different facts.
    /// </summary>
    /// <remarks>
    /// A 405 means "not right now" and GitHub uses it for at least two things that want
    /// opposite answers: the change conflicts with the base (permanent — a retry will
    /// never resolve a conflict, and only a human or another round can), and the base moved
    /// while the pull request was being merged (transient — the next attempt is against the
    /// new base and often succeeds). Which one it is is in the pull request's
    /// <c>mergeable_state</c>, which is a field rather than a sentence, so it is read.
    /// </remarks>
    private static Exception WhyTheMergeWasRefused(
        RepositoryRef repository,
        PullRequestOnRemote pull,
        Answer answer,
        PullRequestOnRemote? now)
    {
        if (answer.Status is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Conflict)
        {
            switch (now?.MergeableState)
            {
                case "dirty":
                    return new PermanentFailure(
                        $"{repository.FullName}#{pull.Number} conflicts with {now.BaseRef}: the change and the base have "
                            + "moved apart, and only another round or a human can settle that, so the factory will not "
                            + "try again on its own");

                case "behind":
                    return new TransientFailure(
                        $"{repository.FullName}'s base moved while this pull request was being merged, so the attempt "
                            + "was refused for timing rather than for the change. Another attempt is against the newer "
                            + "base");

                case "unstable":
                    return new TransientFailure(
                        $"{repository.FullName}#{pull.Number} has required checks that have not passed, so the merge "
                            + "was refused for now. Whether they pass is the repository's own business, and the "
                            + "factory's own ceiling is what bounds how often it asks");

                case "unknown":
                    return new TransientFailure(
                        $"GitHub has not finished working out whether {repository.FullName}#{pull.Number} can be "
                            + "merged, and would not merge it while it says so");

                default:
                    // `clean`, `has_hooks`, `draft`, or a state this client does not know.
                    // Named in GitHubResponse.Unclassifiable rather than retried: the safe
                    // reading of "we do not know" is one attempt and a human.
                    break;
            }
        }

        return GitHubResponse.Refusal(
            answer.Status,
            answer.Headers,
            answer.Body,
            $"merging {repository.FullName}#{pull.Number}");
    }

    /// <summary>
    /// An issue's node id, which is what GitHub's own relationship endpoints take rather than
    /// a number. Resolved rather than composed from a number because the factory does not
    /// have to be right about GitHub's internal id encoding to link two issues, and a
    /// composed id that happens to be wrong fails in a way that looks like a permissions
    /// problem.
    /// </summary>
    private async Task<long> IssueIdAsync(
        RepositoryRef repository,
        int issueNumber,
        string token,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(issueNumber);

        var answer = await AskAsync(
            HttpMethod.Get,
            $"{repository.ApiPath}/issues/{issueNumber}",
            token,
            json: null,
            cancellationToken).ConfigureAwait(false);

        Expect(answer, $"{repository.FullName}#{issueNumber}");

        // The database id, not the number and not the node id: the relationship endpoints
        // below take numbers in their paths and database ids in their bodies, and a node
        // id is a string no REST relationship endpoint reads. This was found on a live
        // repository — the first Grill published its questions issue and then linked it
        // with a 404, because what this method handed the linker was neither of the two
        // identifiers the endpoint asks for.
        var node = Read(answer);
        return node.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
            ? id.GetInt64()
            : throw new PermanentFailure(
                $"GitHub answered for {repository.FullName}#{issueNumber} without the numeric id its own relationship "
                    + "endpoints need, so this factory cannot link it to anything");
    }

    private async Task LinkAsSubIssueAsync(
        RepositoryRef repository,
        int parentNumber,
        long childId,
        string token,
        CancellationToken cancellationToken)
    {
        // GitHub's own shape, read from its documentation rather than from a fake that
        // agrees with this client by construction: the parent's issue number addresses the
        // endpoint, and the child's database id names what is being added under it.
        var answer = await AskAsync(
            HttpMethod.Post,
            $"{repository.ApiPath}/issues/{parentNumber}/sub_issues",
            token,
            json: new Dictionary<string, object?> { ["sub_issue_id"] = childId },
            cancellationToken).ConfigureAwait(false);

        Expect(answer, $"a sub-issue link on {repository.FullName}#{parentNumber}");
    }

    private async Task<PullRequestOnRemote> PostThePullRequestAsync(
        RepositoryRef repository,
        string branch,
        string title,
        string body,
        string baseBranch,
        int issueNumber,
        string token,
        CancellationToken cancellationToken)
    {
        var answer = await AskAsync(
            HttpMethod.Post,
            $"{repository.ApiPath}/pulls",
            token,
            json: new Dictionary<string, object?>
            {
                ["title"] = title,
                ["body"] = body,
                ["head"] = branch,

                // GitHub's own name for the branch the change lands on. The work item's base
                // is where the round was built, and the two are the same by construction:
                // intake resolved the default branch and the round fetched it.
                ["base"] = baseBranch,

                // The factory does not maintain branches, so it does not ask for the
                // permission to. Said here rather than left to the default because the
                // default is a question about somebody else's repository, and this is the
                // one place in the factory that gets to have an opinion about it.
                ["maintainer_can_modify"] = false,
                ["draft"] = false,
            },
            cancellationToken).ConfigureAwait(false);

        Expect(answer, $"a pull request for {branch} on {repository.FullName}");

        var opened = PullFrom(Read(answer));
        _logger.LogInformation(
            "Opened {Repository}#{Pull} from {Branch} onto {Base}, answering issue #{Issue}.",
            repository.FullName,
            opened.Number,
            branch,
            baseBranch,
            issueNumber);

        return opened;
    }

    /// <summary>
    /// One request, and whatever came back — including nothing at all, which is a
    /// classification rather than an absence.
    /// </summary>
    private async Task<Answer> AskAsync(
        HttpMethod method,
        string path,
        string token,
        object? json,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(Accept));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);

        // Required by GitHub, which refuses a request without one. Every request this
        // process makes to GitHub goes through here, so putting it here rather than on a
        // configured client's default headers is what makes "every request carries one"
        // true of the code rather than of a setting somebody can drop. See `UserAgent`.
        //
        // Added without validation, like the API version above: the value is a product
        // token this class owns and there is nothing in it for the header parser to object
        // to, and a parse is one more thing between a request being built and being sent.
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        // The one place the credential is used for anything, and it is used as a header
        // rather than in the URL: a token in a URL is a token in a log line, in a proxy's
        // access log and in anybody's browser history.
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        if (json is not null)
        {
            request.Content = JsonContent.Create(json);
        }

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return new Answer(response.StatusCode, body, response.Headers, $"{method} {path}");
        }
        catch (OperationCanceledException cancelled) when (cancelled.InnerException is TimeoutException)
        {
            // The client's own timeout, and the only clock in this component. The loop's
            // token firing is a different thing and is not caught here.
            throw GitHubResponse.Transport(cancelled, $"call {method} {path}");
        }
        catch (HttpRequestException unreachable)
        {
            throw GitHubResponse.Transport(unreachable, $"call {method} {path}");
        }
    }

    private static void Expect(Answer answer, string what)
    {
        if (answer.Ok)
        {
            return;
        }

        throw GitHubResponse.Refusal(answer.Status, answer.Headers, answer.Body, what);
    }

    private static JsonElement Read(Answer answer)
    {
        try
        {
            using var document = JsonDocument.Parse(answer.Body);
            return document.RootElement.Clone();
        }
        catch (JsonException unreadable)
        {
            // Not one of the cases this client can classify, and named as such rather than
            // retried: an answer that arrived and was not the shape the API documents is not
            // a connection that dropped, and repeating the identical request would come
            // back with the same unreadable body. It is permanent so that it parks rather
            // than burning the attempt budget three times over.
            throw new PermanentFailure(
                $"GitHub answered {answer.Request} with {(int)answer.Status} and a body this client cannot read as "
                    + $"the API's own shape: {unreadable.Message}",
                unreadable);
        }
    }

    /// <summary>The repository a plan ships to, from the project's own URL and nothing else.</summary>
    private static RepositoryRef RepositoryFor(MergePlan plan) => RepositoryRef.Parse(plan.Repository);

    /// <summary>
    /// A pull request as GitHub describes it. Read here rather than on the port's type
    /// because reading GitHub's JSON is this side's job, and a port that carried a parser
    /// would be a port that knew the service's wire format.
    /// </summary>
    private static PullRequestOnRemote PullFrom(JsonElement pull) => new(
        pull.GetProperty("number").GetInt32(),
        pull.TryGetProperty("html_url", out var url) ? url.GetString() ?? string.Empty : string.Empty,
        pull.TryGetProperty("state", out var state) ? state.GetString() ?? string.Empty : string.Empty,
        pull.TryGetProperty("head", out var head) && head.TryGetProperty("sha", out var sha)
            ? sha.GetString() ?? string.Empty
            : string.Empty,
        pull.TryGetProperty("base", out var withBase) && withBase.TryGetProperty("ref", out var baseRef)
            ? baseRef.GetString() ?? string.Empty
            : string.Empty,

        // `merged_at` rather than `state`: GitHub reports a merged pull request as closed,
        // so the state alone cannot tell a merge from a decline — and the difference
        // between them is the whole of what the idempotency argument rests on.
        pull.TryGetProperty("merged_at", out var mergedAt) && mergedAt.ValueKind == JsonValueKind.String,
        pull.TryGetProperty("mergeable_state", out var mergeable)
            ? mergeable.GetString() ?? string.Empty
            : string.Empty);

    /// <summary>The project file being served for this repository, or a refusal naming it.</summary>
    private Project ProjectServing(RepositoryRef repository)
    {
        var project = _projects.Projects.FirstOrDefault(candidate => RepositoryRef.IsSame(candidate.RepoUrl, repository.PushUrl));
        if (project is not null)
        {
            return project;
        }

        // Permanent, and for the reason the round runner gives for the same absence: project
        // files load at start and do not hot reload, so nothing about a second attempt
        // would differ from the first. It also means nothing is pushed and no pull request
        // is opened on a repository this factory was not asked to build for.
        throw new PermanentFailure(
            $"no project file is being served for {repository.FullName}, so there is no credential to push with and "
                + "this factory is not building for that repository. Project files are read at start, so a change to "
                + "one means a restart");
    }

    /// <summary>
    /// The credential, from the name the project declared. Permanent when it is not there,
    /// and the value is used and never said: it goes into a header and into one child
    /// process's environment, and it does not come out of either.
    /// </summary>
    private string TokenFor(Project project)
    {
        if (_credentials.Read(project.GitHubKeyName) is { Length: > 0 } token)
        {
            return token;
        }

        throw new PermanentFailure(
            $"the project {project.Name} names {project.GitHubKeyName} for its GitHub credential, and this process's "
                + "environment does not have it. Nothing can be pushed and no pull request can be opened without one, "
                + "and the value an operator sets is not something a second attempt would change");
    }

    /// <summary>What one request came back with, and the request it was.</summary>
    private sealed record Answer(
        HttpStatusCode Status,
        string Body,
        System.Net.Http.Headers.HttpResponseHeaders? Headers,
        string Request)
    {
        public bool Ok => (int)Status is >= 200 and < 300;

        /// <summary>GitHub's own account of what it objected to, or what it said instead.</summary>
        public string Summary
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Body))
                {
                    return string.Empty;
                }

                try
                {
                    using var document = JsonDocument.Parse(Body);
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("message", out var message)
                        && message.GetString() is { Length: > 0 } said)
                    {
                        return said;
                    }
                }
                catch (JsonException)
                {
                    // Not JSON. GitHub answers some refusals with prose and a 500 with HTML,
                    // and neither is worth a parse failure on the failure path.
                }

                return Body.ReplaceLineEndings(" ").Trim();
            }
        }
    }
}