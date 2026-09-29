namespace AgentFactory.Tests.GitHub;

using System.Net;
using AgentFactory.Failures;
using AgentFactory.GitHub;
using AgentFactory.Tests.Boundary;

/// <summary>
/// What one <c>MergeAsync</c> call does to a repository: push the branch, open the pull
/// request, merge it — and what a second call does, which is most of the argument.
/// </summary>
/// <remarks>
/// Every test here runs the real client, the real git, a real tree with read-only object
/// files, and a real bare repository to push to. Only GitHub's network is gone, and it is
/// replaced by a handler that records what was actually asked for, so the assertions are
/// about requests and about the state of a repository rather than about which class called
/// which.
/// </remarks>
public class MergeTests
{
    [Fact]
    public async Task One_call_pushes_the_branch_opens_a_pull_request_and_merges_it()
    {
        using var merging = new Merging();
        merging.NothingShippedYet();
        merging.Api.Responding(
            "POST",
            "/pulls",
            (HttpStatusCode.Created, GitHubApi.PullRequest(7, merging.Commit, merging.Branch)));
        merging.Api.Responding("PUT", "/pulls/7/merge", (HttpStatusCode.OK, """{"merged": true}"""));

        await merging.Merge();

        // The remote really has it. This is not a test that the factory remembered a path:
        // a bare repository on this machine was pushed to by `git`, and the commit on it is
        // the round's own.
        Assert.Equal(merging.Commit, merging.Remote.CommitOn(merging.Branch));
        Assert.Equal([merging.Branch], merging.Remote.Branches());

        // And the merge is a merge commit, so the pull request is revertible as one commit
        // and the round's own commit is intact on the base branch (story 53).
        var merge = Assert.Single(merging.Api.Requests, request => request.Method == "PUT");
        Assert.Equal(GitHubClient.MergeMethod, merge.Json().GetProperty("merge_method").GetString());

        // Nothing opened twice and nothing asked twice.
        Assert.Equal(1, merging.Api.TimesAsked("POST", "/pulls"));
        Assert.Equal(1, merging.Api.TimesAsked("PUT", "/merge"));
    }

    [Fact]
    public async Task The_pull_request_is_looked_for_before_anything_is_created()
    {
        // The ordering is the whole of the idempotency argument, so it is asserted on the
        // requests themselves rather than inferred from the outcome. A client that pushed
        // first and looked afterwards would pass every other test in this file and would
        // still open a second pull request after a push that landed and a merge that did
        // not.
        using var merging = new Merging();
        merging.NothingShippedYet();
        merging.Api.Responding(
            "POST",
            "/pulls",
            (HttpStatusCode.Created, GitHubApi.PullRequest(7, merging.Commit, merging.Branch)));
        merging.Api.Responding("PUT", "/merge", (HttpStatusCode.OK, """{"merged": true}"""));

        await merging.Merge();

        var shapes = merging.Api.Shapes;
        Assert.Equal("GET", shapes[0].Split(' ')[0]);
        Assert.Contains("/pulls?state=all", shapes[0], StringComparison.Ordinal);
        Assert.Contains("head=NaniSoft%3A", shapes[0], StringComparison.Ordinal);

        // Nothing that writes to a repository comes before the read that establishes there
        // is nothing to write.
        var firstWrite = shapes.FindIndex(shape => shape.StartsWith("POST", StringComparison.Ordinal)
            || shape.StartsWith("PUT", StringComparison.Ordinal));
        var firstLookup = shapes.FindIndex(shape => shape.StartsWith("GET", StringComparison.Ordinal)
            && shape.Contains("/pulls?", StringComparison.Ordinal));

        Assert.True(
            firstLookup >= 0 && firstLookup < firstWrite,
            $"the pull request lookup must come first: {string.Join(" then ", shapes)}");
    }

    [Fact]
    public async Task A_push_that_lands_and_a_merge_that_then_fails_does_not_leave_a_second_pull_request()
    {
        // #7's hard requirement, in writing: "MergeAsync must be idempotent, or must
        // classify a possibly-applied failure as permanent; a merge that half-succeeded and
        // is retried could open two pull requests."
        //
        // The dangerous case is exactly this one — the push landed, so there is a branch on
        // somebody's repository, and the merge did not. A retry that pushed again and
        // opened a pull request again would put two pull requests for one change in front of
        // a human, and the second would be a duplicate of the first by any reading.
        //
        // What makes it safe is that the second call derives the *same* branch, looks for a
        // pull request on it first, finds the one the first attempt opened, and goes
        // straight to merging it. Nothing is created twice, and the branch is pushed once.
        using var merging = new Merging();

        // First attempt: nothing there, so the branch is pushed and a pull request opened,
        // and then the merge is refused because the base moved.
        var attempts = 0;
        // A faked remote that filters the way GitHub filters, because the `state` the client
        // asks for is the thing that decides whether it can see a pull request that has
        // already been merged. A fake that answered every state the same way would let a
        // client that asked for `state=open` pass this test while shipping a change twice.
        merging.Api.Responding(
            "GET",
            "/pulls?",
            request => GitHubApi.Answer.Of(
                HttpStatusCode.OK,
                attempts++ == 0
                    ? "[]"
                    : GitHubApi.PullRequestsMatching(
                        request.PathAndQuery,
                        GitHubApi.PullRequest(7, merging.Commit, merging.Branch))));
        merging.Api.Responding("GET", "/git/ref/heads/", HttpStatusCode.NotFound, """{"message": "Not Found"}""");
        merging.Api.Responding(
            "POST",
            "/pulls",
            (HttpStatusCode.Created, GitHubApi.PullRequest(7, merging.Commit, merging.Branch)));
        merging.Api.Responding(
            "PUT",
            "/merge",
            (HttpStatusCode.MethodNotAllowed, """{"message": "Base branch was modified"}"""));
        merging.Api.Responding(
            "GET",
            "/pulls/7",
            (HttpStatusCode.OK, GitHubApi.PullRequest(7, merging.Commit, merging.Branch, mergeableState: "behind")));

        var refused = await Assert.ThrowsAsync<TransientFailure>(() => merging.Merge());
        Assert.Contains("base moved", refused.Message, StringComparison.OrdinalIgnoreCase);

        // One push, one branch, one pull request — the state the retry has to find.
        Assert.Equal([merging.Branch], merging.Remote.Branches());
        Assert.Equal(merging.Commit, merging.Remote.CommitOn(merging.Branch));

        // The retry, which is what the loop's own merge-retry policy and a reviewer's second
        // approve both do: one more `MergeAsync` for the same work item. The base has caught
        // up by now, so this attempt is answered.
        merging.Api.Responding("PUT", "/merge", (HttpStatusCode.OK, """{"merged": true}"""));
        await merging.Merge();

        // One pull request. One branch. And the branch is at the round's commit, not at
        // something a second push put there.
        Assert.Equal(1, merging.Api.TimesAsked("POST", "/pulls"));
        Assert.Equal([merging.Branch], merging.Remote.Branches());
        Assert.Equal(merging.Commit, merging.Remote.CommitOn(merging.Branch));

        // And the retry merged the pull request the first attempt opened, rather than one
        // of its own.
        var merged = merging.Api.Requests.Where(request => request.Method == "PUT").ToList();
        Assert.Equal(2, merged.Count);
        Assert.All(merged, request => Assert.Contains("/pulls/7/merge", request.PathAndQuery, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Merging_a_change_that_is_already_merged_creates_nothing()
    {
        // The other half of idempotency, and the one a reviewer's second approve hits: the
        // change is on the base branch, `Done` means merged, and there is nothing left to
        // do. A client that pushed and opened again here would put a second pull request
        // proposing a change that is already merged.
        using var merging = new Merging();
        merging.Api.Responding(
            "GET",
            "/pulls?",
            request => GitHubApi.Answer.Of(
                HttpStatusCode.OK,
                GitHubApi.PullRequestsMatching(
                    request.PathAndQuery,
                    GitHubApi.PullRequest(7, merging.Commit, merging.Branch, merged: true))));

        await merging.Merge();

        Assert.DoesNotContain(merging.Api.Shapes, shape => shape.StartsWith("POST", StringComparison.Ordinal));
        Assert.DoesNotContain(merging.Api.Shapes, shape => shape.StartsWith("PUT", StringComparison.Ordinal));
        Assert.Empty(merging.Remote.Branches());

        // And it said so, rather than being a silent success.
        Assert.Contains(merging.Api.Requests, request => request.Method == "GET");
        Assert.Contains("already", merging.Log.Everything, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_pull_request_a_human_closed_is_not_reopened_and_not_merged()
    {
        // A closed pull request is a decline, and the asymmetry the whole loop is built on
        // applies to it: a rejection is conclusive and the factory does not go around it.
        // Re-opening and merging would be shipping a change somebody closed, unattended.
        using var merging = new Merging();
        merging.Api.Responding(
            "GET",
            "/pulls?",
            request => GitHubApi.Answer.Of(
                HttpStatusCode.OK,
                GitHubApi.PullRequestsMatching(
                    request.PathAndQuery,
                    GitHubApi.PullRequest(7, merging.Commit, merging.Branch, state: "closed"))));

        var refused = await Assert.ThrowsAsync<PermanentFailure>(() => merging.Merge());

        Assert.Contains("#7", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(merging.Api.Shapes, shape => shape.StartsWith("PUT", StringComparison.Ordinal));
        Assert.DoesNotContain(merging.Api.Shapes, shape => shape.StartsWith("POST", StringComparison.Ordinal));
        Assert.Equal(FailureClass.Permanent, Failures.Classify(refused));
    }

    [Fact]
    public async Task A_pull_request_whose_branch_moved_on_is_not_merged()
    {
        // The change a reviewer judged is a commit. If the branch has moved on since, the
        // commits on the pull request are not the ones the board showed anybody, and
        // merging would ship work nobody looked at — which is the failure the review surface
        // exists to prevent, arrived at from the other direction.
        using var merging = new Merging();
        var somebodyElse = new string('a', 40);
        merging.Api.Responding(
            "GET",
            "/pulls?",
            request => GitHubApi.Answer.Of(
                HttpStatusCode.OK,
                GitHubApi.PullRequestsMatching(
                    request.PathAndQuery,
                    GitHubApi.PullRequest(7, somebodyElse, merging.Branch))));

        var refused = await Assert.ThrowsAsync<PermanentFailure>(() => merging.Merge());

        Assert.Contains("moved on", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(merging.Api.Shapes, shape => shape.StartsWith("PUT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_branch_somebody_else_owns_is_never_overwritten()
    {
        // There is no `--force` anywhere in this factory, and this is the property that
        // says so: a branch that is already on the remote at a different commit is somebody
        // else's, whether that is a human, a hook, or a second factory on the same
        // repository.
        using var merging = new Merging();
        var theirs = new string('b', 40);
        merging.Api.Responding("GET", "/pulls?", (HttpStatusCode.OK, "[]"));
        merging.Api.Responding("GET", "/git/ref/heads/", (HttpStatusCode.OK, GitHubApi.Ref(theirs)));

        var refused = await Assert.ThrowsAsync<PermanentFailure>(() => merging.Merge());

        Assert.Contains("does not force a branch", refused.Message, StringComparison.Ordinal);
        Assert.Empty(merging.Remote.Branches());
    }

    [Fact]
    public async Task A_round_that_left_changes_uncommitted_is_not_shipped()
    {
        // The board's diff is `git diff` against the base of the *working tree*, so it shows
        // a round's uncommitted files along with its commits. A push sends only what was
        // committed. Shipping the committed part and calling it the reviewed change would
        // make Done mean something the reviewer never saw, and committing the difference on
        // the host would make the host the author of work nobody reviewed (ADR-0006).
        using var merging = new Merging();
        merging.Tree.LeftUncommitted("src/Scratch.cs", "// the agent left this behind\n");
        merging.NothingShippedYet();

        var refused = await Assert.ThrowsAsync<PermanentFailure>(() => merging.Merge());

        Assert.Contains("never committed", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(merging.Remote.Branches());
        Assert.Empty(merging.Api.Requests);
    }

    [Fact]
    public async Task A_round_whose_tree_never_came_out_of_its_container_cannot_be_shipped()
    {
        // The host holds the only copy of a round's change (ADR-0006): the container is gone
        // and there is nothing to re-fetch. There is no retry into existence for this, so it
        // is permanent and it is refused before anything leaves the building.
        using var merging = new Merging();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(merging.TreePath, ".git", "objects"), "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(merging.TreePath, recursive: true);

        var refused = await Assert.ThrowsAsync<PermanentFailure>(() => merging.Merge());

        Assert.Contains("not on this host", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(merging.Api.Requests);
    }

    [Fact]
    public async Task A_repository_this_factory_is_not_serving_is_not_pushed_to()
    {
        // The merger pushes under a project's credential, so a repository it was not asked
        // to build for is refused rather than pushed to. Project files load at start, so a
        // second attempt would find exactly the same thing.
        using var merging = new Merging();
        var unserved = new AgentFactory.Projects.ProjectLoadReport([], []);
        var client = new GitHubClient(
            new HttpClient(merging.Api) { BaseAddress = new Uri("https://api.github.com/") },
            unserved,
            merging.Options,
            merging.Store,
            new FakeCredentialReader().Having(Merging.KeyName, Merging.Token),
            new RecordingLogger<GitHubClient>());

        var refused = await Assert.ThrowsAsync<PermanentFailure>(
            () => client.MergeAsync(merging.WorkItem.RepoUrl, 42, CancellationToken.None));

        Assert.Contains("no project file is being served", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(merging.Api.Requests);
    }

    [Fact]
    public async Task A_project_whose_credential_is_not_in_the_environment_ships_nothing()
    {
        // The name is named, because that is what an operator has to go and fix. The value
        // is not asked about and not logged, because there is not one.
        using var merging = new Merging();
        var client = new GitHubClient(
            new HttpClient(merging.Api) { BaseAddress = new Uri("https://api.github.com/") },
            new AgentFactory.Projects.ProjectLoadReport(
                [new AgentFactory.Projects.Project(
                    "nexus",
                    merging.WorkItem.RepoUrl,
                    "image",
                    "anthropic",
                    "NEXUS_GITHUB_TOKEN",
                    "NEXUS_LLM_KEY",
                    "nexus.yaml")],
                []),
            merging.Options,
            merging.Store,
            new FakeCredentialReader(),
            new RecordingLogger<GitHubClient>());

        var refused = await Assert.ThrowsAsync<PermanentFailure>(
            () => client.MergeAsync(merging.WorkItem.RepoUrl, 42, CancellationToken.None));

        Assert.Contains("NEXUS_GITHUB_TOKEN", refused.Message, StringComparison.Ordinal);
        Assert.Empty(merging.Api.Requests);
        Assert.Equal(FailureClass.Permanent, Failures.Classify(refused));
    }

    [Fact]
    public async Task The_credential_is_asked_for_by_name_and_never_appears_in_anything_the_factory_says()
    {
        // ADR-0006 is a claim about the host, and the host is where the token lives: the
        // project declares a name, the name is resolved once, and the value goes into a
        // header and into one git child's environment. It must not come out of either — not
        // in a log line, not in the message of the failure a reviewer reads on the board,
        // and not in a URL that would put it in a proxy's access log.
        using var merging = new Merging();
        merging.NothingShippedYet();
        merging.Api.Responding(
            "POST",
            "/pulls",
            (HttpStatusCode.Created, GitHubApi.PullRequest(7, merging.Commit, merging.Branch)));
        merging.Api.Responding("PUT", "/merge", (HttpStatusCode.Unauthorized, """{"message": "Bad credentials"}"""));
        merging.Api.Responding("GET", "/pulls/7", (HttpStatusCode.Unauthorized, """{"message": "Bad credentials"}"""));

        var refused = await Assert.ThrowsAsync<PermanentFailure>(() => merging.Merge());

        Assert.Equal([Merging.KeyName], merging.CredentialsAsked);
        Assert.DoesNotContain(Merging.Token, merging.Log.Everything, StringComparison.Ordinal);
        Assert.DoesNotContain(Merging.Token, refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Merging.Token, refused.ToString(), StringComparison.Ordinal);
        Assert.All(merging.Api.Requests, request =>
        {
            Assert.DoesNotContain(Merging.Token, request.PathAndQuery, StringComparison.Ordinal);
            Assert.DoesNotContain(Merging.Token, request.Body, StringComparison.Ordinal);
        });

        // The one place it is meant to be: an Authorization header on every request. Never a
        // query parameter, never a path segment.
        Assert.All(merging.Api.Requests, request =>
            Assert.Equal($"Bearer {Merging.Token}", request.Authorization));
        Assert.All(merging.Api.Requests, request =>
            Assert.DoesNotContain("access_token", request.PathAndQuery, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_push_git_refused_is_classified_by_asking_the_remote_rather_than_by_reading_git()
    {
        // git has no status code, and its prose is not a contract, so the merger does not
        // read it. It asks the remote two questions instead: is the branch there now, and can
        // the repository be reached at all. Here it can be reached — it answers a request
        // made straight after git gave up — so git's refusal is the remote declining, and
        // asking again would be declined the same way.
        using var merging = new Merging();
        merging.Api.Responding("GET", "/pulls?", (HttpStatusCode.OK, "[]"));
        merging.Api.Responding("GET", "/git/ref/heads/", (HttpStatusCode.NotFound, """{"message": "Not Found"}"""));
        merging.Api.Responding("GET", "repos/NaniSoft/nexus", (HttpStatusCode.OK, """{"full_name": "NaniSoft/nexus"}"""));

        // The remote is gone, so git cannot push to it. The API fakes are untouched, which
        // is what makes this hermetic: nothing leaves the machine either way.
        Directory.Delete(merging.Remote.Path, recursive: true);

        var refused = await Assert.ThrowsAsync<PermanentFailure>(() => merging.Merge());

        Assert.Contains("the remote declining", refused.Message, StringComparison.OrdinalIgnoreCase);

        // Exactly one reachability probe, and it is the last request: the question is asked
        // only after git has refused and the remote has been shown not to have the branch.
        var probe = Assert.Single(
            merging.Api.Requests,
            request => request.Method == "GET" && request.PathAndQuery == "/repos/NaniSoft/nexus");
        Assert.Same(merging.Api.Requests[^1], probe);
        Assert.Equal(0, merging.Api.TimesAsked("POST", "/pulls"));
    }

    [Fact]
    public async Task A_push_that_could_not_happen_because_nothing_was_reachable_is_transient()
    {
        // The same two questions, the other answer. Here the repository cannot be reached
        // either, so the push failed for want of a connection rather than for want of
        // permission — which is the whole difference between an attempt that might work in
        // ten seconds and one that will be refused the same way for ever.
        using var merging = new Merging();
        merging.Api.Responding("GET", "/pulls?", (HttpStatusCode.OK, "[]"));
        merging.Api.Responding("GET", "/git/ref/heads/", (HttpStatusCode.NotFound, """{"message": "Not Found"}"""));
        merging.Api.Responding("GET", "repos/NaniSoft/nexus", GitHubApi.Answer.RateLimited("""{"message": "rate limited"}"""));

        Directory.Delete(merging.Remote.Path, recursive: true);

        var refused = await Assert.ThrowsAsync<TransientFailure>(() => merging.Merge());

        Assert.Contains("not there rather than an answer from the remote", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, merging.Api.TimesAsked("POST", "/pulls"));
    }

    [Fact]
    public async Task A_merge_that_conflicts_is_permanent_and_a_merge_that_raced_is_transient()
    {
        // One status code, two facts, and the retry policy needs them told apart. A change
        // that conflicts with the base is a human's or another round's problem; a base that
        // moved while the pull request was being merged is a race the next attempt wins.
        // GitHub's prose cannot be told apart by reading it, so the pull request's own
        // `mergeable_state` is: a field, not a sentence.
        foreach (var (state, expected) in new[]
        {
            ("dirty", typeof(PermanentFailure)),
            ("behind", typeof(TransientFailure)),
            ("unstable", typeof(TransientFailure)),
            ("unknown", typeof(TransientFailure)),
            ("clean", typeof(PermanentFailure)),
        })
        {
            using var merging = new Merging();
            merging.Api.Responding("GET", "/pulls?", (HttpStatusCode.OK, "[]"));
            merging.Api.Responding("GET", "/git/ref/heads/", (HttpStatusCode.NotFound, """{"message": "Not Found"}"""));
            merging.Api.Responding(
                "POST",
                "/pulls",
                (HttpStatusCode.Created, GitHubApi.PullRequest(7, merging.Commit, merging.Branch)));
            merging.Api.Responding("PUT", "/merge", (HttpStatusCode.MethodNotAllowed, """{"message": "Pull Request is not mergeable"}"""));
            merging.Api.Responding(
                "GET",
                "/pulls/7",
                (HttpStatusCode.OK, GitHubApi.PullRequest(7, merging.Commit, merging.Branch, mergeableState: state)));

            var refused = await Assert.ThrowsAnyAsync<FactoryFailure>(() => merging.Merge());

            Assert.Equal(expected, refused.GetType());
        }
    }

    [Fact]
    public async Task A_pull_request_that_answers_200_without_merging_is_refused_rather_than_reported_as_shipped()
    {
        // The last unclassifiable case the merger can be handed, and the one that matters
        // most: `Done` means merged, so a 200 that did not merge is not a merge. It is
        // permanent, because nothing about asking again makes the same answer mean something
        // else.
        using var merging = new Merging();
        merging.Api.Responding("GET", "/pulls?", (HttpStatusCode.OK, "[]"));
        merging.Api.Responding("GET", "/git/ref/heads/", (HttpStatusCode.NotFound, """{"message": "Not Found"}"""));
        merging.Api.Responding(
            "POST",
            "/pulls",
            (HttpStatusCode.Created, GitHubApi.PullRequest(7, merging.Commit, merging.Branch)));
        merging.Api.Responding(
            "PUT",
            "/merge",
            (HttpStatusCode.OK, """{"merged": false, "message": "Pull Request is not mergeable"}"""));
        merging.Api.Responding(
            "GET",
            "/pulls/7",
            (HttpStatusCode.OK, GitHubApi.PullRequest(7, merging.Commit, merging.Branch, mergeableState: "clean")));

        var refused = await Assert.ThrowsAsync<PermanentFailure>(() => merging.Merge());

        Assert.Contains("did not merge", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_second_call_of_two_never_pushes_again_because_the_remote_is_asked_first()
    {
        // The other half of idempotency, and the half that protects a repository rather than
        // a reviewer: the remote is asked whether the branch is already there *before* git
        // is, so a call that follows a successful push does not push at all. Git would
        // refuse the second push anyway — a branch already at a commit is already up to date
        // — but asking first means the factory knows the difference between "there is
        // nothing to send" and "git had nothing to do", and logs the first rather than
        // making a second write against somebody's repository to find out.
        using var merging = new Merging();
        var pushed = merging.Commit;

        // First call: nothing on the remote, so the branch is pushed and the merge lands.
        merging.Api.Responding("GET", "/pulls?", (HttpStatusCode.OK, "[]"));
        merging.Api.Responding("GET", "/git/ref/heads/", _ => GitHubApi.Answer.NotFound());
        merging.Api.Responding(
            "POST",
            "/pulls",
            (HttpStatusCode.Created, GitHubApi.PullRequest(7, pushed, merging.Branch)));
        merging.Api.Responding("PUT", "/merge", (HttpStatusCode.OK, """{"merged": true}"""));

        await merging.Merge();
        Assert.Equal(1, merging.Api.TimesAsked("GET", "/git/ref/heads/"));

        // Second call, with the branch now on the remote. The lookup finds no open pull
        // request — GitHub has closed the merged one — so the push step runs again, and it
        // is the *remote* that says there is nothing to do.
        merging.Api.Responding("GET", "/pulls?", (HttpStatusCode.OK, "[]"));
        merging.Api.Responding("GET", "/git/ref/heads/", (HttpStatusCode.OK, GitHubApi.Ref(pushed)));
        merging.Api.Responding(
            "POST",
            "/pulls",
            (HttpStatusCode.Created, GitHubApi.PullRequest(8, pushed, merging.Branch)));
        merging.Api.Responding("PUT", "/merge", (HttpStatusCode.OK, """{"merged": true}"""));

        await merging.Merge();

        // Two pulls, because this test deliberately answered "no pull request" both times —
        // it is about the *push*, and the second push was skipped because the branch was
        // already at this commit. A second git push would have been refused by git; the
        // point is that none was attempted and none was needed.
        Assert.Contains("nothing to push", merging.Log.Everything, StringComparison.Ordinal);
        Assert.Equal([merging.Branch], merging.Remote.Branches());
        Assert.Equal(pushed, merging.Remote.CommitOn(merging.Branch));
    }

    [Fact]
    public async Task A_merge_that_landed_despite_not_saying_so_is_taken_as_a_merge_that_worked()
    {
        // The mirror of the case above, and the reason a retry cannot ship twice: GitHub
        // accepted the merge and the answer was lost — a timeout, a dropped connection, a
        // process restarted. Reading the pull request back says it is merged, so the change
        // shipped and the factory says so rather than opening a second pull request for a
        // change already on the base branch.
        using var merging = new Merging();
        merging.Api.Responding("GET", "/pulls?", (HttpStatusCode.OK, "[]"));
        merging.Api.Responding("GET", "/git/ref/heads/", (HttpStatusCode.NotFound, """{"message": "Not Found"}"""));
        merging.Api.Responding(
            "POST",
            "/pulls",
            (HttpStatusCode.Created, GitHubApi.PullRequest(7, merging.Commit, merging.Branch)));

        // An answer that arrived and did not say it merged. GitHub's merge endpoint has
        // answered in this shape — a message and no `merged` field — and the client has to
        // notice rather than read a 200 as a merge, because `Done` means merged.
        merging.Api.Responding(
            "PUT",
            "/merge",
            (HttpStatusCode.OK, """{"message": "Pull Request successfully merged"}"""));
        merging.Api.Responding(
            "GET",
            "/pulls/7",
            (HttpStatusCode.OK, GitHubApi.PullRequest(7, merging.Commit, merging.Branch, merged: true)));

        await merging.Merge();

        Assert.Equal(1, merging.Api.TimesAsked("POST", "/pulls"));
        Assert.Equal([merging.Branch], merging.Remote.Branches());

        // A merge whose answer never arrived at all is a different case, and it is not
        // special-cased: the call throws a transient failure, the work item parks, and the
        // retry finds the merged pull request and does nothing. The idempotency is what
        // covers it, which is the same property the test above asserts.
    }

    [Fact]
    public async Task A_second_pull_request_is_never_opened_even_when_the_first_attempt_is_the_one_that_raced()
    {
        // The window the read-before-write step cannot close by itself: two things asking at
        // once, both finding nothing. GitHub refuses the second create with a 422, and the
        // merger closes the window by asking again rather than by reading the 422's message
        // — if a pull request is there now, that is the one to merge, and if there is not,
        // the 422 was about something else and is a real refusal.
        using var merging = new Merging();
        var asks = 0;
        merging.Api.Responding(
            "GET",
            "/pulls?",
            request => GitHubApi.Answer.Of(
                HttpStatusCode.OK,
                asks++ == 0
                    ? "[]"
                    : GitHubApi.PullRequestsMatching(
                        request.PathAndQuery,
                        GitHubApi.PullRequest(7, merging.Commit, merging.Branch))));
        merging.Api.Responding("GET", "/git/ref/heads/", (HttpStatusCode.NotFound, """{"message": "Not Found"}"""));
        merging.Api.Responding("POST", "/pulls", (HttpStatusCode.UnprocessableEntity, """{"message": "A pull request already exists for NaniSoft:agent-factory/42-the-poller-stops-when-a-project-is-paced"}"""));
        merging.Api.Responding("PUT", "/merge", (HttpStatusCode.OK, """{"merged": true}"""));

        await merging.Merge();

        Assert.Equal(1, merging.Api.TimesAsked("POST", "/pulls"));
        Assert.Equal(1, merging.Api.TimesAsked("PUT", "/pulls/7/merge"));
        Assert.Contains("refused to open a second pull request", merging.Log.Everything, StringComparison.Ordinal);
    }
}
