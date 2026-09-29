namespace AgentFactory.GitHub;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentFactory.Containers;
using AgentFactory.Credentials;
using AgentFactory.Failures;
using AgentFactory.Observability;
using AgentFactory.Projects;
using AgentFactory.WorkItems;
using Microsoft.Extensions.Logging;

/// <summary>
/// GitHub, over its real API, from the host. Intake reads through it and the merger writes
/// through it, which is the whole reason it is one class behind one seam: a factory that
/// polled one repository's issues from one place and pushed to another could disagree with
/// itself about what a repository is.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What one <see cref="MergeAsync"/> call does.</strong> The loop says one thing —
/// "land this work item's change in this repository" — and everything else is here:
/// </para>
/// <list type="number">
/// <item><description>Resolve the project, its credential, the work item behind the issue
/// number, the round's tree on this host, and the commit that tree is on.</description></item>
/// <item><description><em>Look for a pull request for this branch first.</em> Read before
/// write, and this is the step that makes a second call safe.</description></item>
/// <item><description>Push the commit, unless the branch is already on the remote at exactly
/// that commit — which is what a previous attempt's push looks like.</description></item>
/// <item><description>Open the pull request, or use the one that is already there.</description></item>
/// <item><description>Merge it, and only if the branch still carries the commit the reviewer
/// judged.</description></item>
/// </list>
/// <para>
/// The order is the argument, and the second step is where it is made. A retry after a
/// push that landed and a merge that did not finds the pull request the first attempt
/// opened and goes straight to merging it, so it cannot open a second one; and because the
/// branch is named deterministically from the issue, "this branch" is the same branch on
/// every attempt. The two windows git and GitHub themselves close are described where they
/// are closed.
/// </para>
/// <para>
/// <strong>Failures are declared here</strong>, from status codes and headers, at the point
/// the answer was read — see <see cref="GitHubResponse"/>. Nothing above this class reads a
/// message to decide anything, and this class does not either: where GitHub's only signal
/// is prose, the caller asks GitHub a second question instead, and the cases that remain are
/// named in <see cref="GitHubResponse.Unclassifiable"/> rather than guessed at.
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
/// that ships it (ADR-0006).
/// </para>
/// </remarks>
public sealed class GitHubClient : IGitHub
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
    private readonly FactoryOptions _options;
    private readonly IWorkItemStore _store;
    private readonly ICredentialReader _credentials;
    private readonly ILogger<GitHubClient> _logger;

    public GitHubClient(
        HttpClient http,
        ProjectLoadReport projects,
        FactoryOptions options,
        IWorkItemStore store,
        ICredentialReader credentials,
        ILogger<GitHubClient> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _store = store ?? throw new ArgumentNullException(nameof(store));
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
    public async Task<PullRequest> OpenPullRequestAsync(
        PullRequestRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var repository = RepositoryRef.Parse(request.RepoUrl);
        var workItem = WorkItemFor(repository, request.IssueNumber);
        var token = TokenFor(ProjectServing(repository));

        var opened = await OpenThePullRequestAsync(
            repository,
            workItem,
            request.Branch,
            request.Title,
            request.Body,
            token,
            cancellationToken).ConfigureAwait(false);

        return new PullRequest(opened.Number, opened.Url);
    }

    /// <inheritdoc />
    public async Task MergeAsync(string repoUrl, int issueNumber, CancellationToken cancellationToken)
    {
        // Everything that can be refused without leaving the building is refused first, so
        // a misconfigured project, an absent credential, a work item this process has never
        // heard of and a round whose tree never came out of its container are all answered
        // without a request and without a git process. Four of those five are facts about
        // this machine or this configuration, and none of them changes in ten seconds.
        var repository = RepositoryRef.Parse(repoUrl);
        var project = ProjectServing(repository);
        var token = TokenFor(project);
        var workItem = WorkItemFor(repository, issueNumber);
        var tree = TreeFor(workItem);
        var commit = await CommitAt(tree, cancellationToken).ConfigureAwait(false);
        var branch = BranchName.For(issueNumber, workItem.IssueTitle);

        // The merger is the one component that holds no work item until it has gone looking
        // for one, and this is where it has it: from here on, every record this method
        // writes — including the ones its private methods write — carries the work item the
        // change belongs to. Shipping is the last thing that happens to a work item, and a
        // record of a push or a merge that names only `project#13` is a record a reader has
        // to join to a work item by hand, which is the join this scope removes.
        using var trace = _logger.ForWorkItem(workItem.Id, workItem.Project, workItem.IssueNumber);

        _logger.LogInformation(
            "Shipping {Project}#{Issue}: the change is commit {Commit} on branch {Branch}, pushed from {Tree}.",
            project.Name,
            issueNumber,
            Short(commit),
            branch,
            tree);

        // **Read before write.** Whether a pull request already exists is asked first,
        // before anything is created and before the branch is pushed, and it is the step
        // that makes this whole method safe to call twice. Every attempt for one work item
        // derives the same branch, so "the pull request for this branch" is a question with
        // one answer, and this is where it is asked.
        var existing = await FindThePullRequestAsync(repository, branch, token, cancellationToken)
            .ConfigureAwait(false);

        if (existing is { Merged: true })
        {
            // Already shipped. Not an error and not a second merge: `Done` means merged, and
            // the change is merged, so a second approve or a second attempt at a parked
            // merge has nothing left to do and says so rather than opening anything.
            _logger.LogInformation(
                "The change for {Project}#{Issue} is already in {Repository}#{Pull} and merged, so there is "
                    + "nothing left to ship.",
                project.Name,
                issueNumber,
                repository.FullName,
                existing.Number);

            return;
        }

        if (existing is { Merged: false, State: "closed" })
        {
            // A human closed this pull request without merging it. That is the same kind of
            // act as a rejection, and the asymmetry the whole loop is built on applies here
            // too: a decline is conclusive and the factory does not go around it. Permanent,
            // and refused with the pull request named so it can be looked at.
            throw new PermanentFailure(
                $"{repository.FullName}#{existing.Number} — the pull request for this change — was closed without "
                    + $"being merged, so the factory will not open another or merge over that. It is a decline, and "
                    + "the way out of one is a human's, from the board");
        }

        // An open pull request means the branch is already on the remote, so the push below
        // is only for the case where there is nothing there yet.
        if (existing is null)
        {
            await PushAsync(repository, tree, commit, branch, token, cancellationToken).ConfigureAwait(false);
        }

        var pull = existing ?? await OpenThePullRequestAsync(
            repository,
            workItem,
            branch,
            PullRequestText.Title(workItem),
            PullRequestText.Body(workItem, branch),
            token,
            cancellationToken).ConfigureAwait(false);

        // The pull request has to be the change a reviewer judged. If something else has
        // been pushed onto the factory's branch since — a human fixing a conflict, a hook,
        // a second machine running the same factory — the commits on it are not the ones
        // that were on the board, and merging would ship work nobody looked at. Refused,
        // permanently: it is not going to become the reviewed change again on its own.
        if (pull.HeadSha is { Length: > 0 } head && !string.Equals(head, commit, StringComparison.Ordinal))
        {
            throw new PermanentFailure(
                $"{repository.FullName}#{pull.Number} carries commit {Short(head)} on {branch}, and this round's "
                    + $"change is {Short(commit)}: the branch has moved on since the reviewer judged it, so merging "
                    + "it would ship work the board never showed anybody");
        }

        await MergeThePullRequestAsync(repository, pull, branch, token, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Merged {Repository}#{Pull} — the change for {Project}#{Issue} — as a {Method} commit on {Branch}.",
            repository.FullName,
            pull.Number,
            project.Name,
            issueNumber,
            MergeMethod,
            branch);
    }

    /// <summary>
    /// The pull request already open against this branch, or null if there is none.
    /// </summary>
    /// <remarks>
    /// <c>state=all</c>, because "no pull request" has to mean no pull request rather than
    /// no <em>open</em> one: a retry after a merge that GitHub accepted but this process
    /// never saw the answer to would otherwise find nothing, push again, and open a second
    /// pull request for a change that is already on the base branch. When several come back
    /// the highest number wins, which is the most recent one — the only one that could be
    /// the one this process is looking for.
    /// </remarks>
    private async Task<RemotePullRequest?> FindThePullRequestAsync(
        RepositoryRef repository,
        string branch,
        string token,
        CancellationToken cancellationToken)
    {
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
            .Select(RemotePullRequest.From)
            .OrderByDescending(pull => pull.Number)
            .FirstOrDefault();
    }

    /// <summary>
    /// Gets the branch onto the remote, or establishes that it is already there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The remote is asked about the branch <em>before</em> git is, and that read is what
    /// makes the push idempotent rather than merely repeatable: a second attempt for a work
    /// item whose push already landed finds the branch at the same commit and sends
    /// nothing at all. A branch that exists at a <em>different</em> commit is refused
    /// permanently, because there is no <c>--force</c> anywhere in this factory and a
    /// branch somebody else owns is not this factory's to overwrite.
    /// </para>
    /// <para>
    /// A push git refuses is then classified by asking the remote two further questions
    /// rather than by reading git's output, because git has no status code and its prose is
    /// not a contract. If the branch is now at this commit, the push landed and git did not
    /// say so. If it is at another commit, the branch is not ours. If it is not there at all,
    /// then whether this factory could reach the remote *at all* decides: if it could not,
    /// the push failed because nothing was reachable and that is transient; if it could,
    /// then git's refusal is the remote's own answer and repeating it would only be
    /// refused the same way.
    /// </para>
    /// </remarks>
    private async Task PushAsync(
        RepositoryRef repository,
        string tree,
        string commit,
        string branch,
        string token,
        CancellationToken cancellationToken)
    {
        var onRemote = await RemoteBranchAsync(repository, branch, token, cancellationToken).ConfigureAwait(false);

        if (string.Equals(onRemote, commit, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "{Branch} is already on {Repository} at {Commit}, which is this change, so there is nothing to push.",
                branch,
                repository.FullName,
                Short(commit));

            return;
        }

        if (onRemote is { Length: > 0 } somebodyElse)
        {
            throw new PermanentFailure(
                $"{repository.FullName} already has a branch {branch}, at commit {Short(somebodyElse)}, and this "
                    + $"round's change is {Short(commit)}. The factory does not force a branch, so this change "
                    + "cannot be shipped on it and nothing has been pushed");
        }

        var pushed = await GitPusher.PushAsync(
            tree,
            repository.PushUrl,
            commit,
            branch,
            GitPusher.BasicAuthorization(token),
            cancellationToken).ConfigureAwait(false);

        if (pushed.Succeeded)
        {
            return;
        }

        // What did the remote actually do? The two questions, in the order that decides.
        var now = await RemoteBranchAsync(repository, branch, token, cancellationToken).ConfigureAwait(false);

        if (string.Equals(now, commit, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "git did not report pushing {Branch} to {Repository} ({Reason}), but the branch is there at "
                    + "{Commit}, so it landed and this is treated as a push that worked.",
                branch,
                repository.FullName,
                pushed.Summary,
                Short(commit));

            return;
        }

        if (now is { Length: > 0 } movedOn)
        {
            throw new PermanentFailure(
                $"{repository.FullName} has a branch {branch} at commit {Short(movedOn)} after git refused to push "
                    + $"this change's commit {Short(commit)} to it. The factory does not force a branch, so nothing "
                    + "has been pushed");
        }

        if (!await TheRemoteIsReachableAsync(repository, token, cancellationToken).ConfigureAwait(false))
        {
            throw new TransientFailure(
                $"git could not push {branch} to {repository.FullName} ({pushed.Summary}), and the repository could "
                    + "not be reached either, so this is a connection that was not there rather than an answer from "
                    + "the remote");
        }

        throw new PermanentFailure(
            $"git refused to push {branch} to {repository.FullName} — {pushed.Summary} — and the branch is not "
                + "there. The repository answered a request made straight after it, so this is the remote declining "
                + "the push rather than a connection that dropped, and asking again would be refused the same way");
    }

    /// <summary>
    /// The commit a branch is at on the remote, or null when the branch is not there.
    /// </summary>
    /// <remarks>
    /// A 404 is the answer "no such branch" and only that. Anything else is a refusal, and
    /// a refusal on this read is the same failure the push would be: it happens before the
    /// push on a first attempt and after one on a retry.
    /// </remarks>
    private async Task<string?> RemoteBranchAsync(
        RepositoryRef repository,
        string branch,
        string token,
        CancellationToken cancellationToken)
    {
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

    /// <summary>
    /// Whether the repository can be reached at all, answered by making the cheapest
    /// request there is. The answer is a classification rather than a boolean, and that is
    /// the point: a request that came back as a refusal proves the remote answered, so a
    /// push git refused was refused by the remote; a request that never came back proves
    /// the opposite, and the push failed for want of a connection.
    /// </summary>
    private async Task<bool> TheRemoteIsReachableAsync(
        RepositoryRef repository,
        string token,
        CancellationToken cancellationToken)
    {
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

    private async Task<RemotePullRequest> OpenThePullRequestAsync(
        RepositoryRef repository,
        WorkItem workItem,
        string branch,
        string title,
        string body,
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
                ["base"] = workItem.BaseBranch,

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
            var raced = await FindThePullRequestAsync(repository, branch, token, cancellationToken)
                .ConfigureAwait(false);

            if (raced is not null)
            {
                _logger.LogWarning(
                    "GitHub refused to open a second pull request for {Branch} on {Repository}, and there is one "
                        + "there now (#{Pull}), so this attempt will finish that one instead.",
                    branch,
                    repository.FullName,
                    raced.Number);

                return raced;
            }
        }

        Expect(answer, $"a pull request for {branch} on {repository.FullName}");

        var opened = RemotePullRequest.From(Read(answer));
        _logger.LogInformation(
            "Opened {Repository}#{Pull} from {Branch} onto {Base}, answering issue #{Issue}.",
            repository.FullName,
            opened.Number,
            branch,
            workItem.BaseBranch,
            workItem.IssueNumber);

        return opened;
    }

    private async Task MergeThePullRequestAsync(
        RepositoryRef repository,
        RemotePullRequest pull,
        string branch,
        string token,
        CancellationToken cancellationToken)
    {
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
        var now = await ReadThePullRequestAsync(repository, pull.Number, token, cancellationToken)
            .ConfigureAwait(false);

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
    private Exception WhyTheMergeWasRefused(
        RepositoryRef repository,
        RemotePullRequest pull,
        Answer answer,
        RemotePullRequest? now)
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

    private async Task<RemotePullRequest?> ReadThePullRequestAsync(
        RepositoryRef repository,
        int number,
        string token,
        CancellationToken cancellationToken)
    {
        var answer = await AskAsync(
            HttpMethod.Get,
            $"{repository.ApiPath}/pulls/{number}",
            token,
            json: null,
            cancellationToken).ConfigureAwait(false);

        // A read that fails is not a reason to change the answer to the merge. The refusal
        // the caller already has is the honest one; this read is only there to say whether
        // the merge landed despite it.
        if (!answer.Ok)
        {
            _logger.LogWarning(
                "Could not read {Repository}#{Pull} back after a merge that did not report one: {Reason}",
                repository.FullName,
                number,
                GitHubResponse.Refusal(answer.Status, answer.Headers, answer.Body, "reading a pull request back").Message);

            return null;
        }

        return RemotePullRequest.From(Read(answer));
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

    /// <summary>
    /// The work item an issue number is, which is the record the branch, the pull request
    /// and the tree are all named from.
    /// </summary>
    /// <remarks>
    /// Read from the store rather than fetched from GitHub, deliberately. The work item is
    /// the factory's own record of the issue — number, title, base branch — and it is what
    /// the reviewer read on the board. A pull request titled from GitHub's copy of the issue
    /// would be a second source of truth about the same thing, free to disagree with the
    /// board; and the branch name is derived from the title, so a title that changed on
    /// GitHub between the round and the merge would move the branch out from under a push
    /// that had already happened.
    /// </remarks>
    private WorkItem WorkItemFor(RepositoryRef repository, int issueNumber)
    {
        var workItem = _store.List()
            .FirstOrDefault(candidate => candidate.IssueNumber == issueNumber
                && RepositoryRef.IsSame(candidate.RepoUrl, repository.PushUrl));

        if (workItem is not null)
        {
            return workItem;
        }

        throw new PermanentFailure(
            $"this factory has no work item for {repository.FullName}#{issueNumber}, so there is no change of its own "
                + "to ship. The loop asks the merger about work items it holds, so reaching here means the call and the "
                + "record have come apart");
    }

    /// <summary>
    /// The round's tree on this host, which is where its commit is. The host pushes, so a
    /// round whose tree did not come out of its container has nothing to push — the
    /// container is gone and there is no second copy of the change (ADR-0006).
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is the <em>latest</em> round's tree, and that is the whole of the change this
    /// ticket made to this method. The path is per round rather than per work item now —
    /// <c>docker cp</c> copies a directory into a destination that already exists rather
    /// than replacing it, so one directory per work item put round 2's tree inside round
    /// 1's (#22) — and a merger that still looked in the work item's own directory would be
    /// pushing round 1's commit while the reviewer believed they were judging round 2.
    /// </para>
    /// <para>
    /// The latest round with a tree, rather than the latest round: a round whose tree never
    /// came out is not a reason to ship an earlier one, so the search walks back until it
    /// finds a tree and says which round it is shipping if it cannot find one at all.
    /// </para>
    /// </remarks>
    private string TreeFor(WorkItem workItem)
    {
        var rounds = _store.Rounds(workItem.Id);

        foreach (var round in Enumerable.Reverse(rounds))
        {
            if (round.Diff is { } diff && Directory.Exists(Path.Combine(diff.Tree, ".git")))
            {
                return diff.Tree;
            }
        }

        // Named by the rounds that had none, so the refusal says which round the reviewer
        // was looking at rather than only that the host has nothing.
        throw new PermanentFailure(
            $"none of the {rounds.Count} round(s) of {workItem.Project}#{workItem.IssueNumber} left a tree on this "
                + "host, so there is no commit to push. The containers that made the changes are gone and the host "
                + "holds the only copy of them, so this cannot be retried into existence and the change has to be "
                + "built again");
    }

    /// <summary>
    /// The commit to ship, and the check that the tree holds nothing else.
    /// </summary>
    /// <remarks>
    /// The uncommitted check is the whole of "the diff a reviewer judges is the diff that
    /// would ship". The board's diff is <c>git diff</c> against the base of the working
    /// tree, so it shows a round's uncommitted files along with its commits; a push sends
    /// only what was committed. Shipping a change that is not the one on the board would
    /// break the one property the review surface exists for, and committing the difference
    /// here would make the host the author of work nobody reviewed, which is the thing
    /// ADR-0006 exists to prevent. So the two are refused against each other, and a human
    /// finishes it from the board.
    /// </remarks>
    private async Task<string> CommitAt(string tree, CancellationToken cancellationToken)
    {
        var head = await GitPusher
            .RunAsync(tree, GitPusher.HeadArguments(), authorization: null, cancellationToken)
            .ConfigureAwait(false);

        if (!head.Succeeded)
        {
            throw new PermanentFailure(
                $"the round's tree at {tree} has no commit to push: {head.Summary}. A round that committed nothing "
                    + "produced no change for the host to ship, and the base it was built from is not this change");
        }

        var commit = head.Output.Trim();
        if (commit.Length == 0)
        {
            throw new PermanentFailure($"the round's tree at {tree} named no commit at HEAD");
        }

        var uncommitted = await GitPusher
            .RunAsync(tree, GitPusher.UncommittedArguments(), authorization: null, cancellationToken)
            .ConfigureAwait(false);

        if (!uncommitted.Succeeded)
        {
            throw new PermanentFailure(
                $"the round's tree at {tree} would not say what it is holding uncommitted: {uncommitted.Summary}. The "
                    + "factory will not push a change it cannot account for");
        }

        if (uncommitted.Output.Trim() is { Length: > 0 } leftBehind)
        {
            throw new PermanentFailure(
                $"the round left changes in its tree that it never committed ({Describe(leftBehind)}), so what the "
                    + "factory would push is not what the reviewer judged on the board. Committing them here would "
                    + "make the host the author of work nobody reviewed, so this is left for a human");
        }

        return commit;
    }

    /// <summary>What git's porcelain said, in a form a reviewer can read on a board.</summary>
    private static string Describe(string porcelain) => string.Join(
        ", ",
        porcelain
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Take(3)
            .Select(line => line.Length <= 60 ? line : line[..57] + "…"));

    private static string Short(string commit) => commit.Length > 7 ? commit[..7] : commit;

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

    /// <summary>
    /// A pull request as GitHub describes it, which is the only account of it that
    /// decides anything here.
    /// </summary>
    private sealed record RemotePullRequest(int Number, string Url, string State, string HeadSha, string BaseRef, bool Merged, string MergeableState)
    {
        public static RemotePullRequest From(JsonElement pull) => new(
            pull.GetProperty("number").GetInt32(),
            pull.TryGetProperty("html_url", out var url) ? url.GetString() ?? string.Empty : string.Empty,
            pull.TryGetProperty("state", out var state) ? state.GetString() ?? string.Empty : string.Empty,
            pull.TryGetProperty("head", out var head) && head.TryGetProperty("sha", out var sha)
                ? sha.GetString() ?? string.Empty
                : string.Empty,
            pull.TryGetProperty("base", out var withBase) && withBase.TryGetProperty("ref", out var baseRef)
                ? baseRef.GetString() ?? string.Empty
                : string.Empty,
            // `merged_at` rather than `state`: GitHub reports a merged pull request as
            // closed, so the state alone cannot tell a merge from a decline — and the
            // difference between them is the whole of what the idempotency argument rests
            // on.
            pull.TryGetProperty("merged_at", out var mergedAt) && mergedAt.ValueKind == JsonValueKind.String,
            pull.TryGetProperty("mergeable_state", out var mergeable)
                ? mergeable.GetString() ?? string.Empty
                : string.Empty);
    }
}
