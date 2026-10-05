namespace AgentFactory.Tests.Review;

using AgentFactory.Containers;
using AgentFactory.Failures;
using AgentFactory.Projects;
using AgentFactory.Results;
using AgentFactory.Rounds;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;
using Xunit.Sdk;

/// <summary>
/// The diff in Review, over the board's real HTTP surface (story 34, #11).
/// </summary>
/// <remarks>
/// <para>
/// The centre of the ticket is one claim: a reviewer judges the change, not a description
/// of it. Almost everything here follows from taking that literally, and the two places
/// it bites hardest are the two the ticket named as undecided — how a large diff is
/// presented, and whether the review surface depends on GitHub being up. The answer to
/// the second is that it must not, and the reason is not only availability: a diff
/// fetched from the same service that produced the change is a second opinion from the
/// thing under review, not evidence about it.
/// </para>
/// <para>
/// Most of these run a fake agent, so the round's diff is what a test wrote. One does not:
/// <see cref="The_diff_on_the_board_is_git_diff_of_the_tree_a_real_round_left"/> runs the
/// real round runner over a real git repository with only Docker faked, because a diff
/// asserted against a fixture the test itself wrote is a fixture agreeing with itself.
/// </para>
/// </remarks>
public class DiffOnTheBoardTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";

    // ---------------------------------------------------------------- how it is generated

    [Fact]
    public async Task The_diff_on_the_board_is_git_diff_of_the_tree_a_real_round_left()
    {
        // The one test here that runs the round for real. Everything else can be faked
        // because it is about how the board renders a diff; this is about where the diff
        // comes from, and the answer has to be a real `git diff` against a real tree or
        // it is not an answer at all.
        //
        // The only substitution is the Docker CLI, which hands over a genuine repository.
        // The round runner, the container runtime, the deriver, the host's diff reader and
        // the board are all the real thing, and the round ends in Review having been
        // asked for by the real loop.
        using var tree = LiftedTree.WithACommitOn();
        tree.Committed("add the endpoint", ("src/Endpoint.cs", "public sealed class Endpoint { }\n"));

        // The project's base branch moves on while the round was running, which is ordinary
        // and is the reason the diff is taken against the commit the round *started* at
        // rather than against the branch's current tip. Diffed against the tip, the card
        // would also show a commit the round never saw and never wrote.
        tree.BaseBranchMovedOn(
            "a commit the round never saw",
            "src/Unrelated.cs",
            "public sealed class Unrelated { }\n");

        // And the object files are read-only, which is what a tree lifted out of a Linux
        // container looks like on a Windows host. The diff is a read of them, and nothing
        // here may need to write to or delete them to get one.
        tree.WithReadOnlyObjects();

        using var root = FactoryRoot.Create()
            .WithProjectFile("nexus.yaml", ProjectFile.Valid);

        var docker = new FakeDockerCli();
        docker.ContainerTrees[ContainerRuntime.WorkPathInContainer] = tree.Path;

        // A result file shaped like the one the image's collector writes, carrying the
        // commit the round started from — the base the host's diff reader must prefer.
        docker.ContainerFiles[ContainerRuntime.ResultPathInContainer] = ResultFile(tree.StartHead);

        await using var host = await FactoryHost.StartWithTheRealRoundAsync(root, docker);
        var promoted = host.Store.Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(promoted.Id);

        // Not Settle: the real round runner copies a tree off disk and runs git against
        // it, so the round is genuinely in flight across calls and the machine has to be
        // stepped until it holds nothing.
        await host.RunTheMachineAsync();

        Assert.Equal(Swimlane.Review, Assert.Single(host.Store.List()).Swimlane);

        var diff = (await Board.ReadAsync(host.Board)).DiffOn(1);

        // The line that identifies the change is git's, for the file the commit touched,
        // and git said it was a new file. A board showing anything else is showing a
        // description.
        Assert.Equal("shown", diff.State);
        var file = Assert.Single(diff.Files);
        Assert.Equal("src/Endpoint.cs", file.Path);
        Assert.Equal("added", file.Change);
        Assert.Contains("diff --git a/src/Endpoint.cs b/src/Endpoint.cs", file.Text, StringComparison.Ordinal);
        Assert.Contains("new file mode", file.Text, StringComparison.Ordinal);
        Assert.Contains("+public sealed class Endpoint { }", file.Text, StringComparison.Ordinal);

        // The one file on the card is the file the round wrote, and nothing else. Had the
        // base been the branch's current tip rather than the round's own start commit, the
        // unrelated commit made while the round was running would be on it too — a real
        // diff, of changes the round did not make, presented as the round's work.
        Assert.DoesNotContain("Unrelated", diff.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("README.md", diff.Text, StringComparison.Ordinal);

        // And it really was generated here rather than handed over: the tree the fake
        // lifted out is named on the card, and it is the tree the round left.
        Assert.Contains("tree", diff.Tree, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(diff.Tree), $"the board named {diff.Tree} as the round's tree, and there is no such directory");
    }

    [Fact]
    public void The_rounds_diff_is_not_annotated_by_the_hosts_line_ending_setting()
    {
        // #26, and the honest shape of it.
        //
        // What the run actually observed was a warning, not a wrong diff: on this host,
        // `core.autocrlf=true`, and `git diff` in the lifted tree printed "LF will be
        // replaced by CRLF the next time Git touches it" for every file in the change. The
        // diff could not be made *wrong* in that run, and it is worth saying so plainly
        // rather than overstating the ticket: what was at risk was the claim that a
        // reviewer holds the card against their own `git diff` and gets the same thing.
        //
        // So this asserts the observable half directly, over the real git and the real
        // arguments the reader passes. A round leaves uncommitted work on disk as often as
        // it commits, and a committed change is compared blob-to-blob where autocrlf has no
        // say at all — so the uncommitted case is the one that reproduces, and it is the
        // one the deriver and the merger's own "left changes uncommitted" check also depend
        // on.
        using var tree = LiftedTree.WithACommitOn();

        // What the host's own configuration produced. `core.autocrlf=true` is on the
        // *system* config of the machine this was found on; it is reproduced in the tree's
        // own repository so the test does not reach outside the factory and edit a
        // developer's global git config, which would be a worse version of the same
        // dependency.
        tree.Configured("core.autocrlf", "true");
        tree.LeftUncommitted("src/Index.cs", "public sealed class Index { }\npublic sealed class Added { }\n");

        var annotated = Git(tree, tree.StartHead, withLineEndingPin: false);

        // The exact sentence #16 recorded, because that is what a reviewer reading the log
        // would have seen and a looser match would let a different warning stand in for it.
        Assert.Contains("LF will be replaced by CRLF", annotated.Error, StringComparison.Ordinal);

        // What the reader passes, on the same tree, in the same state.
        var pinned = Git(tree, tree.StartHead, withLineEndingPin: true);

        Assert.Equal(string.Empty, pinned.Error.Trim());
        Assert.Equal(annotated.Output, pinned.Output);
    }

    /// <summary>
    /// One <c>git diff</c> against a real tree, either with the reader's own argument list
    /// or with the line-ending pin left off, so the difference between them is the thing
    /// under test rather than an accident of how the process was started.
    /// </summary>
    private static (string Output, string Error) Git(
        LiftedTree tree,
        string against,
        bool withLineEndingPin)
    {
        var arguments = HostDiffReader.Arguments(against)
            .ToList();

        if (!withLineEndingPin)
        {
            // The pair the reader pins, dropped as a pair — a `-c` and the value after it.
            for (var at = 0; at < arguments.Count; at++)
            {
                if (arguments[at] == "-c" && arguments[at + 1] == "core.autocrlf=false")
                {
                    arguments.RemoveRange(at, 2);
                    break;
                }
            }
        }

        return LiftedTree.Git(tree.Path, [.. arguments]);
    }

    /// <summary>
    /// One round's diff as the board rendered it, from a real tree, with the real round
    /// runner and only Docker faked. The single path <see cref="LiftedTree"/> exists for,
    [Fact]
    public async Task Round_twos_diff_is_round_twos_change_and_not_round_ones()
    {
        // **The decisive test for #22, and the one that was impossible to write before it.**
        //
        // Two real rounds of one work item, each in its own real git repository with a
        // different change committed, run through the real round runner and the real
        // container runtime and rendered by the real board. The fake Docker CLI hands out a
        // different tree per round — which it can only do because it now behaves like the
        // real `docker cp`, which copies a directory *into* a destination that already
        // exists rather than replacing it.
        //
        // What the first real run found is that round 2's tree landed at `tree/work/`
        // inside round 1's, so round 2's card showed round 1's diff. The trees here are
        // deliberately different, so a nested copy is not a copy that happens to look
        // identical: the assertion below is about which *change* is on round 2's card, and
        // against the old code it fails on the file, not on a path.
        using var first = LiftedTree.WithACommitOn();
        first.Committed("round one answers the question", ("src/First.cs", "public sealed class First { }\n"));
        first.WithReadOnlyObjects();

        using var second = LiftedTree.WithACommitOn();
        second.Committed("round two answers it differently", ("src/Second.cs", "public sealed class Second { }\n"));
        second.WithReadOnlyObjects();

        using var root = FactoryRoot.Create()
            .WithProjectFile("nexus.yaml", ProjectFile.Valid);

        var docker = new FakeDockerCli();

        // One tree per round, handed over in the order the rounds ask for them. This is a
        // queue rather than a dictionary because two rounds of one work item lift the same
        // container path and have to come away with different trees — which is exactly the
        // situation the old one-directory-per-work-item layout made impossible to see.
        var perRound = new Queue<string>([first.Path, second.Path]);
        var perRoundStarts = new Queue<string>([first.StartHead, second.StartHead]);

        docker.Handler = call =>
        {
            if (call.Arguments.FirstOrDefault() == "create")
            {
                docker.ContainerTrees[ContainerRuntime.WorkPathInContainer] = perRound.Dequeue();
                docker.ContainerFiles[ContainerRuntime.ResultPathInContainer] = ResultFile(perRoundStarts.Dequeue());
            }

            return Task.FromResult(new DockerInvocation(0, string.Empty));
        };

        await using var host = await FactoryHost.StartWithTheRealRoundAsync(root, docker);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(workItem.Id);

        await host.RunTheMachineAsync();
        using (await Board.DecideAsync(host.Board, workItem.Id, "request-changes", "Try it differently."))
        {
        }

        await host.RunTheMachineAsync();

        var board = await Board.ReadAsync(host.Board);
        var roundOne = board.DiffOn(1);
        var roundTwo = board.DiffOn(2);

        // Each round's card carries its own change, which is the whole claim. Before this
        // ticket round 2's card carried `src/First.cs` — round 1's file — because round
        // 2's tree had been copied inside round 1's.
        Assert.Equal(["src/First.cs"], roundOne.Files.Select(file => file.Path));
        Assert.Equal(["src/Second.cs"], roundTwo.Files.Select(file => file.Path));

        // Each card carries its own file's git text and not the other's, so this is a claim
        // about the bytes a reviewer reads rather than about a list of names.
        Assert.Contains("+public sealed class First", roundOne.Files.Single().Text, StringComparison.Ordinal);
        Assert.Contains("+public sealed class Second", roundTwo.Files.Single().Text, StringComparison.Ordinal);
        Assert.DoesNotContain("First.cs", roundTwo.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Second.cs", roundOne.Text, StringComparison.Ordinal);

        // And the two trees are two directories, not one directory with something in it.
        // A nested copy also leaves `git status` reporting `?? work/`, which is the
        // merger's "left changes uncommitted" refusal — so a nesting makes every later
        // round permanently unshippable for a reason unrelated to its work.
        Assert.NotEqual(roundOne.Tree, roundTwo.Tree);
        Assert.All(
            new[] { roundOne.Tree, roundTwo.Tree },
            tree => Assert.True(Directory.Exists(Path.Combine(tree, ".git")), $"{tree} is not a tree of its own"));
        Assert.Empty(Directory
            .EnumerateDirectories(Path.GetDirectoryName(roundOne.Tree)!, "work", SearchOption.AllDirectories));

        // The two rounds' trees are both still on the host, which is what makes the merger
        // able to ship either of them: the host pushes, and the container that made each one
        // is long gone (ADR-0006).
        Assert.Equal(2, host.Store.Rounds(workItem.Id).Count);
    }

    [Fact]
    public async Task A_round_whose_own_command_did_not_succeed_is_not_reported_as_a_round_that_changed_nothing()
    {
        // The review surface's half of #22, over the real board.
        //
        // The run found `data-outcome="Produced" data-diff-state="empty"` and the sentence
        // "Empty. Nothing on disk differs from the commit the round started at" over a
        // round whose `opencode run` had exited 1 on a provider rate limit in two and a half
        // seconds, having written nothing. Every one of those attributes was defensible on
        // its own and the sentence together was a confident false claim about a round that
        // never started, which is what a reviewer's first two decisions were made against.
        // The outcome and classification here are the ones a reviewer's card would carry for
        // a rate limit the retry policy has spent. `Permanent` rather than `Transient` only
        // because this test is about what the card *says*, and a transient one is asked for
        // again three times first — the classification itself is asserted where it is
        // decided, in `WorkerRoundRunnerTests`.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundResult.Failed(
            FailureClass.Permanent,
            "worker-round: result=/out/result.json roundExitCode=1\n"
                + "Error: Error from provider (Console): Rate limit exceeded. Please try again later.",
            AChangeTo(),
            "outcome THE ROUND'S OWN COMMAND EXITED 1: the round did not run to completion, and nothing below is a "
                + "finished result\ncommands run, and what each returned\n  exit 1    2.5s   the agent, building and "
                + "testing the change\n            opencode run --standalone --auto\n            │ Error: Error from provider "
                + "(Console): Rate limit exceeded. Please try again later."));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(workItem.Id);

        await host.Settle();

        var board = await Board.ReadAsync(host.Board);
        var diff = board.DiffOn(1);

        // Not `empty`. A fourth state, and its name is about the round rather than the
        // disk — because the disk really is unchanged, and saying so as `empty` is what
        // made the claim a lie.
        Assert.Equal("unfinished", diff.State);

        // And it says why, in a sentence a reviewer can act on, rather than asserting that
        // a decision was made.
        Assert.Contains("Unfinished", diff.Text, StringComparison.Ordinal);
        Assert.Contains("did not run to completion", diff.Text, StringComparison.Ordinal);
        Assert.Contains("not because the round chose to leave it alone", diff.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Empty. Nothing on disk differs", diff.Text, StringComparison.Ordinal);

        // The round is `Failed` and carries the payload, so the exit code and the provider's
        // own words are both on the card rather than only in the log.
        Assert.Equal("Failed", board.Rendered("data-round", "1", "data-outcome"));
        Assert.Equal("Permanent", board.Rendered("data-round", "1", "data-failure"));
        Assert.Equal("true", board.Rendered("data-round", "1", "data-has-result"));
        Assert.Contains("Rate limit exceeded", board.ResultOn(1)!, StringComparison.Ordinal);

        // Escalated, where a human can still merge or decline it — a round that produced
        // nothing is not something to send a reviewer to Review for.
        Assert.Equal(Swimlane.Escalated, host.Store.Get(workItem.Id)!.Swimlane);
    }

    [Fact]
    public async Task A_round_that_ran_and_changed_nothing_is_still_empty()
    {
        // The other half, and the one that must not have moved. A round whose own last
        // command returned zero and which left the tree exactly as it found it *is* "the
        // round ran and decided there was nothing to do", and a reviewer is entitled to be
        // told that. The fourth state is for the round that did not run to completion, and
        // only for that.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundResult.Produced(
            ResultPayload.Of(Derived(diff: string.Empty), "the round read the tree and stopped"),
            "Nothing needed changing.",
            "the round said it read the tree and stopped",
            AChangeTo()));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var promoted2 = host.Store.Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(promoted2.Id);

        await host.Settle();

        var diff = (await Board.ReadAsync(host.Board)).DiffOn(1);

        Assert.Equal("empty", diff.State);
        Assert.Contains("Empty", diff.Text, StringComparison.Ordinal);
        Assert.Contains("the round ran to completion and changed nothing", diff.Text, StringComparison.Ordinal);
        Assert.Equal("Produced", (await Board.ReadAsync(host.Board)).Rendered("data-round", "1", "data-outcome"));
    }

    [Fact]
    public async Task A_round_that_changed_nothing_but_did_not_finish_says_so_rather_than_being_empty()
    {
        // The two states are told apart on a real board, from the two facts that tell them
        // apart, rather than asserted once each in isolation. The diffs are byte-identical —
        // an unchanged disk — and the outcomes are not, so anything reading the diff alone
        // would render the same sentence for both.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode()
            .Yielding(RoundResult.Produced(
                ResultPayload.Of(Derived(diff: string.Empty), "read the tree"),
                "Read the tree and stopped.",
                "the round read the tree and stopped",
                AChangeTo()))
            .Yielding(RoundResult.Failed(
                FailureClass.Permanent,
                "worker-round: roundExitCode=1",
                AChangeTo(),
                "outcome THE ROUND'S OWN COMMAND EXITED 1"));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(workItem.Id);

        await host.Settle();
        using (await Board.DecideAsync(host.Board, workItem.Id, "request-changes", "Again."))
        {
        }

        await host.Settle();

        var board = await Board.ReadAsync(host.Board);

        // The same empty diff, two different claims.
        Assert.Equal("empty", board.DiffOn(1).State);
        Assert.Equal("unfinished", board.DiffOn(2).State);
        Assert.DoesNotContain("Unfinished", board.DiffOn(1).Text, StringComparison.Ordinal);
        Assert.Contains("Unfinished", board.DiffOn(2).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rounds_diff_survives_a_restart_so_a_reviewer_can_still_read_the_change()
    {
        // The store is the store of record (ADR-0009), and a diff the board had to
        // re-generate from a directory it no longer has is a diff that is gone. The change
        // is kept with the round, not looked up on render — so this starts the factory
        // again over the same root and reads the same card.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundResult.Produced(
            ResultPayload.Of(Derived()),
            "Added the endpoint.",
            "the round's log",
            AChangeTo("src/Index.cs")));

        await using (var first = await FactoryHost.StartAsync(root, agent: agent))
        {
            var replaced = first.Store.Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await first.PromoteAsync(replaced.Id);
            await first.Settle();
        }

        await using var restarted = await FactoryHost.StartAsync(root);

        var diff = (await Board.ReadAsync(restarted.Board)).DiffOn(1);
        Assert.Equal("shown", diff.State);
        Assert.Equal("src/Index.cs", Assert.Single(diff.Files).Path);
    }

    [Fact]
    public async Task A_round_that_changed_nothing_says_so_rather_than_rendering_no_diff()
    {
        // "This round changed nothing" and "there is no diff for this round" are different
        // claims, and the second one must never be rendered as the first. An empty diff is a
        // real outcome — a round that read the tree and stopped — and it is said in words.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundResult.Produced(
            ResultPayload.Of(Derived(diff: string.Empty)),
            "Nothing needed changing.",
            "the round's log",
            AChangeTo()));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var promoted3 = host.Store.Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(promoted3.Id);

        await host.Settle();

        var board = await Board.ReadAsync(host.Board);
        var diff = board.DiffOn(1);

        Assert.Equal("empty", diff.State);
        Assert.Empty(diff.Files);
        Assert.Contains("Nothing on disk differs", diff.Text, StringComparison.Ordinal);
        Assert.Contains("Nothing needed changing.", board.Read("Review"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_round_whose_diff_could_not_be_generated_says_why_rather_than_showing_nothing()
    {
        // A container that never got as far as producing a tree has no diff to show, and
        // the board has to say that rather than render an empty section — which would read
        // as a round that changed nothing, the worst available answer.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundResult.Failed(
            FailureClass.Permanent,
            "worker-round: cloning\nfatal: the collector was killed",
            HostDiff.Unavailable(
                "/nowhere/rounds/abc/tree",
                "the round's tree did not come out of its container, so there is nothing on the host to diff")));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(workItem.Id);

        await host.Settle();

        var diff = (await Board.ReadAsync(host.Board)).DiffOn(1);

        Assert.Equal("unavailable", diff.State);
        Assert.Empty(diff.Files);
        Assert.Contains("There is no diff for this round", diff.Text, StringComparison.Ordinal);
        Assert.Contains("did not come out of its container", diff.Text, StringComparison.Ordinal);

        // The log is still there beside it, which is the other half of story 29: a round
        // with nothing but a log is not invisible.
        Assert.Equal(Swimlane.Escalated, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Contains("the collector was killed", (await Board.ReadAsync(host.Board)).LogOn(1)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_round_with_no_diff_on_record_at_all_is_not_rendered_as_a_round_that_changed_nothing()
    {
        // A round recorded before the diff existed — a work item from an older database
        // file, or one whose tree was never lifted — must not be given an empty diff to
        // fill the gap. Fabricating one would claim a round changed nothing when in fact
        // nobody looked, which is the whole failure the rendering exists to prevent. The
        // card says there is no diff rather than showing an empty section.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundResult.Produced(
            ResultPayload.Of(Derived()),
            "Added the endpoint.",
            "the round's log"));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var promoted4 = host.Store.Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(promoted4.Id);

        await host.Settle();

        var board = await Board.ReadAsync(host.Board);

        Assert.False(board.RenderedDiffOn(1));
        Assert.Equal("none", board.Rendered("data-round", "1", "data-diff-state"));
    }

    // ---------------------------------------------------------------- without GitHub

    [Fact]
    public async Task The_diff_renders_with_github_refusing_every_call()
    {
        // The property, stated as a test: the board's review surface does not depend on
        // GitHub being reachable. The seam here is the factory's own refusal — the one the
        // composition root registers when no client exists — and every call through it
        // throws, so anything the board renders came from the round's own record.
        //
        // The gap that would be left by a board which fetched a diff from the API is not
        // only availability: a diff read back from the same service that produced the
        // change is a second opinion from the thing under review. This one is evidence.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundResult.Produced(
            ResultPayload.Of(Derived()),
            "Added the endpoint.",
            "the round's log",
            AChangeTo("src/Index.cs")));

        var refusing = new RefusingGitHub();
        await using var host = await FactoryHost.StartAsync(root, agent, github: refusing);
        var promoted5 = host.Store.Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(promoted5.Id);

        await host.Settle();

        // The proof that GitHub is not answering: the seam the running factory holds is
        // the refusal, and it refuses.
        await Assert.ThrowsAsync<NotSupportedException>(
            () => refusing.ListOpenIssuesAsync(RepoUrl, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => refusing.MergeAsync(RepoUrl, 42, CancellationToken.None));

        var board = await Board.ReadAsync(host.Board);
        var diff = board.DiffOn(1);

        // The change is on the page, complete, with the file's own git text under it.
        Assert.Equal("shown", diff.State);
        var file = Assert.Single(diff.Files);
        Assert.Equal("src/Index.cs", file.Path);
        Assert.Contains("+public sealed class Index", file.Text, StringComparison.Ordinal);

        // And nothing on the card mentions GitHub at all, because the review surface does
        // not know it is there.
        Assert.DoesNotContain("github.com", diff.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pull request", board.Read("Review"), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- a large diff

    [Fact]
    public async Task A_reviewer_who_opened_a_file_still_has_it_open_after_the_board_refreshes()
    {
        // Folding is only worth anything if it holds still. The board reloads itself every
        // five seconds — which is the right behaviour for a board and would be the wrong
        // one for a fold — so the open sections are restored on the way back in.
        //
        // Asserted as the shape the restore is written against rather than by running a
        // browser, and the honest limit of that is worth stating plainly: nothing here
        // executes the script, so this does not prove a browser reopened anything. What it
        // does check is the part a reviewer would notice if it were wrong — that every
        // section carries a key naming its work item *and* its round *and* its file, so a
        // file a reviewer opened in round 1 is not also opened in round 2, where the same
        // path is a different change.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode()
            .Yielding(RoundResult.Produced(
                ResultPayload.Of(Derived()),
                "First attempt.",
                "round 1 log",
                AChangeTo("src/First.cs")))
            .Yielding(RoundResult.Produced(
                ResultPayload.Of(Derived()),
                "Second attempt.",
                "round 2 log",
                AChangeTo("src/First.cs", "src/Second.cs")));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(workItem.Id);

        await host.Settle();
        await Board.DecideAsync(host.Board, workItem.Id, "request-changes", "Again.");
        await host.Settle();

        var board = await Board.ReadAsync(host.Board);

        // The same file in both rounds, and a different key for each — which is the whole
        // point, and the thing a key built from the path alone would get wrong.
        Assert.Equal(["src/First.cs"], board.DiffOn(1).Files.Select(file => file.Path));
        Assert.Equal(["src/First.cs", "src/Second.cs"], board.DiffOn(2).Files.Select(file => file.Path));

        var keys = Board.ValuesOf(board.Swimlane("Review"), "data-open-key");
        Assert.Equal(
            [
                $"{workItem.Id:N}/1/src/First.cs",
                $"{workItem.Id:N}/2/src/First.cs",
                $"{workItem.Id:N}/2/src/Second.cs",
            ],
            keys);
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());

        // And the restore reads that key and nothing else.
        Assert.Contains("data-open-key", board.Html, StringComparison.Ordinal);
        Assert.Contains("sessionStorage", board.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_large_diff_is_folded_per_file_with_its_counts_and_nothing_dropped()
    {
        // The judgement #9 left open, and the one worth stating in a test rather than only
        // in a commit message.
        //
        // A diff is shown as one entry per file, with the path, what git says happened,
        // and the line counts on a line the reviewer reads without opening anything —
        // because the shape of a change is what they judge first, and a hundred files
        // scrolled past as one block of text is not a shape. Under each entry is git's
        // own text for that file, unchanged.
        //
        // And nothing is summarised: every file is on the page, every file's counts are
        // right, and the last file's own text is present. A presentation that trimmed or
        // ranked would pass a test that only looked at the first file, which is why this
        // one counts the files and checks the end rather than the top.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundResult.Produced(
            ResultPayload.Of(Derived()),
            "Rewrote everything.",
            "the round's log",
            AChangeTo(Enumerable.Range(1, 12).Select(n => $"src/File{n:00}.cs").ToArray())));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var promoted6 = host.Store.Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(promoted6.Id);

        await host.Settle();

        var diff = (await Board.ReadAsync(host.Board)).DiffOn(1);

        // Every file is there, in git's own order.
        Assert.Equal(12, diff.FilesShown);
        Assert.Equal(12, diff.TotalFiles);
        Assert.Equal(0, diff.OmittedFiles);
        Assert.Equal(
            Enumerable.Range(1, 12).Select(n => $"src/File{n:00}.cs"),
            diff.Files.Select(file => file.Path));

        // Each one's counts are the counts of its own text, so a "+2" is arithmetic a
        // reviewer can check rather than a claim about the change. Two and not three: the
        // `--- /dev/null` and `+++ b/...` lines are file headers, not content, and a
        // counter that read them as content would be over-reporting the change.
        foreach (var file in diff.Files)
        {
            Assert.Equal("added", file.Change);
            Assert.Equal(2, file.Added);
            Assert.Equal(0, file.Removed);
        }

        // And the folding, which is the other half of the decision. One file open so a
        // card shows the change rather than a list of file names, and the rest closed so
        // twelve files do not arrive as a wall — with every file's text on the page either
        // way, which is what makes closing them a presentation choice rather than a
        // summary.
        Assert.True(diff.Files[0].Open, "the first file's change should be visible without a click");
        Assert.All(diff.Files.Skip(1), file => Assert.False(file.Open));
        Assert.All(diff.Files, file => Assert.NotEmpty(file.Text));

        // And the last file's git text is on the page, not just the first one's: a diff
        // that quietly stopped early is the failure this whole rendering exists to prevent.
        var last = diff.Files[^1];
        Assert.Equal("src/File12.cs", last.Path);
        Assert.Contains($"+++ b/src/File12.cs", last.Text, StringComparison.Ordinal);
        Assert.Contains("+public sealed class File12 { }", last.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_diff_too_large_to_render_whole_says_how_much_is_left_off_and_where_to_find_it()
    {
        // The other half of the folding decision, and the one that could have gone wrong
        // quietly. A page cannot hold an unbounded diff, so something has to give — and
        // what gives is *stated*, counted, and points at the tree rather than being
        // summarised away.
        //
        // Three things are asserted, and each is a different failure if it is wrong: that
        // the omitted count is right (a diff that stops without saying how much it stopped
        // at is indistinguishable from a diff of a smaller change), that the shown files
        // are whole rather than cut mid-hunk (half a file looks like all of a file), and
        // that the path to the rest is named (the tree is on the host because the host
        // pushes, ADR-0006 — so it is somewhere a reviewer can actually go).
        using var root = FactoryRoot.Create();

        // A thousand files: past any plausible bound, and built as a real diff so the
        // arithmetic under test is the real one.
        var many = Enumerable.Range(1, 1000)
            .Select(n => $"src/Generated/File{n:0000}.cs")
            .ToArray();

        var agent = new FakeNOpenCode().Yielding(RoundResult.Produced(
            ResultPayload.Of(Derived()),
            "Regenerated the client.",
            "the round's log",
            AChangeTo(many)));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var promoted7 = host.Store.Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(promoted7.Id);

        await host.Settle();

        var diff = (await Board.ReadAsync(host.Board)).DiffOn(1);

        // The counts are arithmetic over the whole diff, not over what fitted.
        Assert.Equal(1000, diff.TotalFiles);
        Assert.Equal(diff.FilesShown + diff.OmittedFiles, diff.TotalFiles);
        Assert.True(diff.OmittedFiles > 0, "a thousand files should not all fit on one page");

        // Every file on the page is complete: its own git text, header to last hunk line,
        // and no file's text bleeding into the next one's section.
        foreach (var file in diff.Files)
        {
            Assert.StartsWith($"diff --git a/{file.Path} b/{file.Path}", file.Text, StringComparison.Ordinal);
            Assert.Contains("+public sealed class", file.Text, StringComparison.Ordinal);
            Assert.Equal(1, CountOf(file.Text, "diff --git "));
        }

        // What is left off is said, in words, with the place to get it.
        Assert.Contains("not on this page", diff.Text, StringComparison.Ordinal);
        Assert.Contains("Nothing is summarised in their place", diff.Text, StringComparison.Ordinal);
        Assert.Contains(diff.Tree, diff.Text, StringComparison.Ordinal);
        Assert.NotEmpty(diff.Tree);
    }

    [Fact]
    public async Task A_diff_foldable_per_file_still_renders_gits_own_text_underneath_unchanged()
    {
        // The specific claim behind folding: nothing is rewritten on the way to the page.
        // Not syntax-highlighted, not elided, not collapsed into "… 40 lines …". What is
        // under the index line is what `git diff` printed, so a reviewer can hold the card
        // against their own `git diff` and get the same bytes.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundResult.Produced(
            ResultPayload.Of(Derived()),
            "Added the endpoint.",
            "the round's log",
            new HostDiff(
                """
                diff --git a/src/Index.cs b/src/Index.cs
                index aaaaaaa..bbbbbbb 100644
                --- a/src/Index.cs
                +++ b/src/Index.cs
                @@ -1,3 +1,5 @@
                 namespace Nexus;
                -public sealed class Missing { }
                +public sealed class Index
                +{
                +}
                 // trailing comment
                """,
                string.Empty,
                "/rounds/abc/tree",
                ContainerBounded: false,
                UnavailableBecause: null)));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var promoted8 = host.Store.Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(promoted8.Id);

        await host.Settle();

        var file = Assert.Single((await Board.ReadAsync(host.Board)).DiffOn(1).Files);

        // The context lines are there, including the ones with no sign in front of them,
        // because a diff that dropped context is a diff that hides an unchanged line a
        // reviewer was relying on.
        Assert.Contains(" namespace Nexus;", file.Text, StringComparison.Ordinal);
        Assert.Contains("-public sealed class Missing { }", file.Text, StringComparison.Ordinal);
        Assert.Contains(" // trailing comment", file.Text, StringComparison.Ordinal);
        Assert.Contains("index aaaaaaa..bbbbbbb 100644", file.Text, StringComparison.Ordinal);

        // One removal, three additions, and no others — read from the board's own index.
        Assert.Equal(1, file.Removed);
        Assert.Equal(3, file.Added);
    }

    [Fact]
    public async Task A_renamed_and_a_binary_file_are_named_as_such_rather_than_shown_as_empty()
    {
        // A pure rename has no content and a binary change has no readable content, and
        // both are changes. A view that rendered an empty body for either would read as
        // "git saw this file and there is nothing to it", which is a different and wrong
        // claim.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundResult.Produced(
            ResultPayload.Of(Derived()),
            "Moved a file and changed an image.",
            "the round's log",
            new HostDiff(
                """
                diff --git a/docs/Old.md b/docs/New.md
                similarity index 100%
                rename from docs/Old.md
                rename to docs/New.md
                diff --git a/logo.png b/logo.png
                index 1111111..2222222 100644
                Binary files a/logo.png and b/logo.png differ
                """,
                string.Empty,
                "/rounds/abc/tree",
                ContainerBounded: false,
                UnavailableBecause: null)));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var promoted9 = host.Store.Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(promoted9.Id);

        await host.Settle();

        var files = (await Board.ReadAsync(host.Board)).DiffOn(1).Files;

        var renamed = files.Single(file => file.Path == "docs/New.md");
        Assert.Equal("renamed", renamed.Change);
        Assert.Contains("from docs/Old.md", renamed.Text, StringComparison.Ordinal);

        var binary = files.Single(file => file.Path == "logo.png");
        Assert.True(binary.Binary);
        Assert.Contains("Binary files", binary.Text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- every round

    [Fact]
    public async Task Every_rounds_diff_is_on_the_board_and_not_only_the_latest()
    {
        // A reviewer who has been through three rounds judges the disagreement between
        // them, and a board showing only the last one is a board hiding how the work got
        // here — including the change the reviewer asked to be undone. Each round's diff
        // is its own, generated from its own tree, and all three are on the card.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode()
            .Yielding(RoundResult.Produced(
                ResultPayload.Of(Derived()),
                "First attempt.",
                "round 1 log",
                AChangeTo("src/First.cs")))
            .Yielding(RoundResult.Produced(
                ResultPayload.Of(Derived()),
                "Second attempt.",
                "round 2 log",
                AChangeTo("src/Second.cs")))
            .Yielding(RoundResult.Produced(
                ResultPayload.Of(Derived()),
                "Third attempt.",
                "round 3 log",
                AChangeTo("src/Third.cs")));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(workItem.Id);

        await host.Settle();
        await Board.DecideAsync(host.Board, workItem.Id, "request-changes", "Not like that.");
        await host.Settle();
        await Board.DecideAsync(host.Board, workItem.Id, "request-changes", "Still not like that.");
        await host.Settle();

        var board = await Board.ReadAsync(host.Board);
        var review = board.Read("Review");

        Assert.Equal(3, host.Store.Get(workItem.Id)!.RoundCount);
        Assert.Equal(["src/First.cs"], board.DiffOn(1).Files.Select(file => file.Path));
        Assert.Equal(["src/Second.cs"], board.DiffOn(2).Files.Select(file => file.Path));
        Assert.Equal(["src/Third.cs"], board.DiffOn(3).Files.Select(file => file.Path));

        // Each round's own log too, not only the latest round's.
        Assert.Equal("round 1 log", board.LogOn(1));
        Assert.Equal("round 2 log", board.LogOn(2));
        Assert.Equal("round 3 log", board.LogOn(3));

        // And the round count, which is what tells a reviewer how much of the ceiling is
        // gone.
        Assert.Equal("3", board.Rendered("data-work-item", workItem.Id.ToString("D"), "data-round-count"));
        Assert.Contains("round 3 of 3", review, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_work_item_whose_first_round_left_no_diff_still_shows_the_second_rounds()
    {
        // The asymmetry that matters: a first round with nothing to diff must not take the
        // review surface with it. Round one produced a result but its tree never came out
        // of its container, round two produced a change, and the card says both things —
        // which is only reachable because a round whose diff could not be generated still
        // reaches Review with a payload rather than being parked.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode()
            .Yielding(RoundResult.Produced(
                ResultPayload.Of(Derived()),
                "First attempt, whose tree never arrived.",
                "round 1 log",
                HostDiff.Unavailable("/nowhere/tree", "the round's tree did not come out of its container")))
            .Yielding(RoundResult.Produced(
                ResultPayload.Of(Derived()),
                "Second attempt.",
                "round 2 log",
                AChangeTo("src/Second.cs")));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(workItem.Id);

        await host.Settle();
        await Board.DecideAsync(host.Board, workItem.Id, "request-changes", "Try again.");
        await host.Settle();

        var board = await Board.ReadAsync(host.Board);

        Assert.Equal(2, host.Store.Get(workItem.Id)!.RoundCount);
        Assert.Equal("unavailable", board.DiffOn(1).State);
        Assert.Equal("shown", board.DiffOn(2).State);
        Assert.Equal("src/Second.cs", Assert.Single(board.DiffOn(2).Files).Path);
    }

    // ---------------------------------------------------------------- the rest of the surface

    [Fact]
    public async Task The_commands_the_round_ran_and_what_each_returned_are_beside_the_change()
    {
        // Story 25, and the reason the change is not the only thing on the card. A reviewer
        // judges not only that a change exists but how it was arrived at: which commands
        // the round ran, what each returned, and whether the failing ones are a build that
        // could not get itself right.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundResult.Produced(
            ResultPayload.Of(Derived(
                commands:
                [
                    new CommandOutcome(1, "the unit tests", "dotnet test", "/work", 0, 1800, "42 passed, 0 failed\n", string.Empty, 21, 0),
                    new CommandOutcome(2, "the linter", "dotnet format --verify-no-changes", "/work", 2, 900, string.Empty, "2 files need formatting\n", 0, 27),
                ])),
            "Added the endpoint.",
            "the round's log",
            AChangeTo("src/Index.cs")));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var promoted10 = host.Store.Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(promoted10.Id);

        await host.Settle();

        var board = await Board.ReadAsync(host.Board);
        var result = board.ResultOn(1);

        // The command, its exit code, its label, and what it actually said — the failing
        // one especially, because that is where a build says why.
        Assert.Contains("dotnet test", result!, StringComparison.Ordinal);
        Assert.Contains("exit 0", result, StringComparison.Ordinal);
        Assert.Contains("42 passed, 0 failed", result, StringComparison.Ordinal);
        Assert.Contains("dotnet format --verify-no-changes", result, StringComparison.Ordinal);
        Assert.Contains("exit 2", result, StringComparison.Ordinal);
        Assert.Contains("2 files need formatting", result, StringComparison.Ordinal);

        // And the change is on the same card, so the two are read together.
        Assert.Equal("shown", board.DiffOn(1).State);
    }

    [Fact]
    public async Task The_feedback_threshold_and_the_instant_this_card_would_ship_are_both_on_the_board()
    {
        // Storys 35 and 36. Ignoring a work item merges it, which is the more dangerous of
        // the two endings, so what a reviewer's silence is worth is stated on the card as
        // well as in the header — a timeout nobody can see per work item is a timeout
        // they cannot act on while it still matters (ADR-0008).
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundResult.Produced(
            ResultPayload.Of(Derived()),
            "Added the endpoint.",
            "the round's log",
            AChangeTo("src/Index.cs")));

        await using var host = await FactoryHost.StartAsync(root, agent: agent, autoMerge: true);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(workItem.Id);

        await host.Settle();

        var board = await Board.ReadAsync(host.Board);

        // The threshold itself, as a number checked against the constant rather than
        // against the prose, so the board and the policy cannot disagree about it. Stated
        // in the header, where it is the first thing a reviewer reads.
        Assert.Equal(
            FactoryConstants.FeedbackThreshold.TotalHours.ToString(),
            board.Rendered("data-feedback-threshold", "threshold", "data-hours"));
        Assert.Contains(
            FactoryConstants.FeedbackThresholdText,
            System.Net.WebUtility.HtmlDecode(board.Html),
            StringComparison.Ordinal);

        // And this card's own instant, which is the reviewer's deadline rather than the
        // factory's: the review's start plus the threshold, read off the board. Decoded,
        // because an attribute value is escaped on the way out and a `+` in a UTC offset
        // is one of the characters that gets escaped.
        Assert.Equal(
            host.Store.Get(workItem.Id)!.ReviewStartedUtc!.Value.Add(FactoryConstants.FeedbackThreshold).ToString("O"),
            System.Net.WebUtility.HtmlDecode(
                board.Rendered("data-work-item", workItem.Id.ToString("D"), "data-auto-merge")));
        Assert.Contains("auto-merges at", board.Read("Review"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_diff_a_reviewer_never_had_to_be_on_the_network_to_read_is_the_whole_of_what_the_change_was()
    {
        // The seam note from #10, honoured: nothing on the review surface needs a pull
        // request number, because the loop has no concept of one and the review surface
        // must not require either. The change is the tree the round left, the commit the
        // round started at, and git's own text — which is all knowable before a branch is
        // pushed and long before a pull request exists.
        //
        // #10's `MergeAsync(repoUrl, issueNumber, ct)` takes an issue number for exactly
        // this reason, and a review surface that required a pull request would be the
        // first thing in the factory to have one.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundResult.Produced(
            ResultPayload.Of(Derived()),
            "Added the endpoint.",
            "the round's log",
            AChangeTo("src/Index.cs")));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var promoted11 = host.Store.Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(promoted11.Id);

        await host.Settle();

        var review = (await Board.ReadAsync(host.Board)).Read("Review");

        Assert.DoesNotContain("pull request", review, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("merge request", review, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#" + 42 + "-", review, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_filtered_board_still_renders_the_diff_and_still_refuses_a_fourth_decision()
    {
        // #12's filter is a read, and the diff is part of what a read renders. A narrowing
        // that dropped the change would be a narrowing of what a reviewer can judge, and
        // it would also be the first thing on the board to differ between the filtered and
    // unfiltered views of the same work item.
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundResult.Produced(
            ResultPayload.Of(Derived()),
            "Added the endpoint.",
            "the round's log",
            AChangeTo("src/Index.cs")));

        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = host.Store
            .Intake("nexus", RepoUrl, 42, "Nothing answers", "An endpoint is missing.", "main").WorkItem;
        await host.PromoteAsync(workItem.Id);

        await host.Settle();

        var filtered = await Board.ReadForAsync(host.Board, "nexus");
        Assert.Equal("shown", filtered.DiffOn(1).State);
        Assert.Equal("src/Index.cs", Assert.Single(filtered.DiffOn(1).Files).Path);

        // The decisions are unchanged by the filter, and a fourth is still refused on it.
        Assert.Equal(["approve", "request-changes", "reject"], Board.ValuesOf(filtered.ProjectGroup("nexus", "Review"), "data-decision"));

        using var refused = await Board.PostByHandForProjectAsync(
            host.Board,
            "nexus",
            workItem.Id,
            "merge-it-anyway");

        var page = await Board.ReadAsync(refused);
        Assert.Contains("not one of the three decisions", page.Refusal()!, StringComparison.Ordinal);
        Assert.Empty(host.Store.Decisions(workItem.Id));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// A host diff of a change to the named files, as <c>git diff</c> would write it.
    /// Written here rather than produced by git so that a test can say what shape of diff
    /// it is about; <see cref="The_diff_on_the_board_is_git_diff_of_the_tree_a_real_round_left"/>
    /// is the one that checks the host really is running git.
    /// </summary>
    private static HostDiff AChangeTo(params string[] paths) => AChangeTo(paths, startHead: string.Empty);

    private static HostDiff AChangeTo(IReadOnlyList<string> paths, string startHead)
    {
        var text = string.Join(
            "\n",
            paths.Select(path =>
                string.Join(
                    "\n",
                    [
                        $"diff --git a/{path} b/{path}",
                        "new file mode 100644",
                        "index 0000000..1111111",
                        "--- /dev/null",
                        $"+++ b/{path}",
                        "@@ -0,0 +1,3 @@",
                        $"+public sealed class {System.IO.Path.GetFileNameWithoutExtension(path)} {{ }}",
                        "+",
                    ])));

        return new HostDiff(text, startHead, $"/rounds/{Guid.Empty:N}/tree", ContainerBounded: false, UnavailableBecause: null);
    }

    /// <summary>
    /// A result file in the shape <c>worker-collect</c> writes, carrying the commit the
    /// round started from so the host's diff reader has a base to work from.
    /// </summary>
    private static string ResultFile(string startHead) => string.Join(
        "\n",
        [
            // The header record, carrying the commit the round started from. That is what
            // the host's diff reader prefers as its base, over the branch it was given.
            $$"""{"kind":"result","schema":"agent-factory/worker-result@1","user":"agent","uid":1000,"gitRepo":true,"branch":"main","startHead":"{{startHead}}","head":"{{startHead}}","credentialEnvNames":[],"git":"git version 2.43.0","opencode":"opencode v2.0.18","os":"Ubuntu 24.04"}""",
            """{"kind":"git","field":"status","text":"1 A. N... 100644 100644 100644 0000000 1111111 src/Endpoint.cs"}""",
            """{"kind":"git","field":"diffFromRoundStart","text":"diff --git a/src/Endpoint.cs b/src/Endpoint.cs\nnew file mode 100644\n--- /dev/null\n+++ b/src/Endpoint.cs\n@@ -0,0 +1 @@\n+public sealed class Endpoint { }"}""",
            """{"kind":"git","field":"commitsSinceRoundStart","text":""}""",
            """{"kind":"git","field":"remotes","text":"origin\thttps://github.com/NaniSoft/nexus.git (fetch)"}""",
            """{"kind":"command","seq":1,"label":"the unit tests","argv":["dotnet","test"],"cwd":"/work","exitCode":0,"durationMs":1800,"stdoutTail":"42 passed, 0 failed\n","stderrTail":"","stdoutBytes":21,"stderrBytes":0}""",
            """{"kind":"note","text":"Added the endpoint, because nothing was answering."}""",
        ]);

    /// <summary>
    /// A derived result shaped like a real one, so a test can say what the board renders
    /// without a Docker daemon anywhere.
    /// </summary>
    private static DerivedResult Derived(
        IReadOnlyList<CommandOutcome>? commands = null,
        string diff = """
            diff --git a/src/Index.cs b/src/Index.cs
            index aaaaaaa..bbbbbbb 100644
            --- a/src/Index.cs
            +++ b/src/Index.cs
            @@ -1,3 +1,15 @@
            +public sealed class Index
            +{
            +}
            """) => new(
        [new ChangedFile("src/Index.cs", null, FileChange.Added, 12, 3, true, false)],
        commands ??
        [
            new CommandOutcome(1, "the unit tests", "dotnet test", "/work", 0, 1800, "42 passed, 0 failed\n", string.Empty, 21, 0),
        ],
        diff,
        DiffTruncated: false,
        AgentNote: "Added the endpoint, because nothing was answering.",
        [],
        new RoundEnvironment(
            User: "agent",
            Uid: 1000,
            IsARepository: true,
            Branch: "main",
            StartHead: "aaaaaaa1111111",
            Head: "bbbbbbb2222222",
            Remotes: string.Empty,
            CredentialNames: [],
            Git: "git version 2.43.0",
            Agent: "opencode v2.0.18",
            OperatingSystem: "Ubuntu 24.04"),
        UnreadableLines: 0,
        UnreadableBecause: null);

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal);
             at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}

/// <summary>
/// The GitHub seam with nothing behind it that says so, rather than one that happens not
/// to be called. Every call throws, so a board that rendered a diff by asking the API
/// would fail here rather than quietly pass.
/// </summary>
public sealed class RefusingGitHub : AgentFactory.GitHub.IGitHub
{
    public Task<string> GetDefaultBranchAsync(string repoUrl, CancellationToken cancellationToken) =>
        throw NoGitHub();

    public Task<IReadOnlyList<AgentFactory.GitHub.OpenIssue>> ListOpenIssuesAsync(
        string repoUrl,
        CancellationToken cancellationToken) =>
        throw NoGitHub();

    public Task<AgentFactory.GitHub.PullRequest> OpenPullRequestAsync(
        AgentFactory.GitHub.PullRequestRequest request,
        CancellationToken cancellationToken) =>
        throw NoGitHub();

    public Task MergeAsync(string repoUrl, int issueNumber, CancellationToken cancellationToken) =>
        throw NoGitHub();

    private static NotSupportedException NoGitHub() =>
        new("there is no GitHub behind this seam, and a round's diff does not need one");
}
