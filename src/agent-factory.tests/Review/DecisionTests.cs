namespace AgentFactory.Tests.Review;

using System.Net;
using AgentFactory.Rounds;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;

/// <summary>
/// The three decisions, driven over the board's own HTTP surface — the only write path
/// the factory has, and the only way a work item leaves Review by human action. The
/// store, the loop and the page are the real thing; only the agent and the clock are
/// substituted, and nothing here waits.
/// </summary>
public class DecisionTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";

    private const string IssueBody = "What the issue says, in the maintainer's words.";

    private const string Feedback = "The null check is outside the lock; move it inside, and don't widen the scope to \"fix\" it.";

    private const string LaterFeedback = "Still outside the lock. That is the whole of it.";

    /// <summary>
    /// Each of the three and where it puts the work item. Approve is here with a merger
    /// behind the seam, because Done means merged: approving is complete only once a merge
    /// landed, and the theory is about which lane a decision lands the work item in once
    /// the factory can do what the decision asks. What happens when it cannot is the other
    /// ticket's, and <see cref="ApproveTests"/>'s.
    /// </summary>
    public static TheoryData<string, Swimlane> TheThreeDecisions => new()
    {
        { "approve", Swimlane.Done },
        { "request-changes", Swimlane.Frontier },
        { "reject", Swimlane.Rejected },
    };

    [Fact]
    public async Task Review_exposes_exactly_three_decisions()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = await InReview(host);

        var board = await Board.ReadAsync(host.Board);

        // Three, and no more: a reviewer in Review can approve, send it back, or decline
        // it. The order is the order they are offered in, and the slugs are what the
        // buttons post, so "exactly three" is a claim about the write path and not only
        // about the markup. Nothing anywhere else on the board offers a fourth.
        Assert.Equal(["approve", "request-changes", "reject"], Board.ValuesOf(board.Swimlane("Review"), "data-decision"));
        Assert.Equal(["approve", "request-changes", "reject"], board.ValuesOf("data-decision"));
        Assert.NotEqual(string.Empty, board.DecisionFormFor(workItem.Id));
    }

    [Theory]
    [MemberData(nameof(TheThreeDecisions))]
    public async Task Each_decision_puts_the_work_item_where_it_belongs(string decision, Swimlane expected)
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "Added the endpoint.");

        // A working merger, because Done means merged. Approve is the one decision that
        // asks the factory to do something outside itself before it can land the work item,
        // and this theory is about the lane each decision lands a work item in — not about
        // what happens when the factory cannot do it, which ApproveTests covers.
        var github = new FakeGitHub().Merging();
        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github);
        var workItem = await InReview(host);

        using var response = await Board.DecideAsync(host.Board, workItem.Id, decision, Feedback);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, host.Store.Get(workItem.Id)!.Swimlane);

        // What the reviewer did is kept: which of the three it was, in their own words,
        // stamped by the clock the factory is wired to. The decision is the record; the
        // swimlane it moved to is the loop's answer to it, and the record says which
        // decision did it.
        var recorded = Assert.Single(host.Store.Decisions(workItem.Id));
        Assert.Equal(workItem.Id, recorded.WorkItemId);
        Assert.Equal(1, recorded.Sequence);
        Assert.Equal(Feedback, recorded.Feedback);
        Assert.Equal(host.Clock.UtcNow, recorded.DecidedUtc);
        Assert.Equal(expected, recorded.AppliedTo);
        Assert.False(recorded.IsPending);

        // The board says where it went and why, rather than the work item going quiet.
        // The reviewer's words come back as they were written, quotes and all, because
        // they are the record and not a summary of it.
        var board = await Board.ReadAsync(host.Board);
        Assert.Contains(workItem.Id.ToString(), board.Swimlane(Swimlanes.Label(expected)), StringComparison.Ordinal);
        Assert.DoesNotContain(workItem.Id.ToString(), board.Swimlane("Review"), StringComparison.Ordinal);
        var rendered = Assert.Single(board.DecisionsOn(workItem.Id));
        Assert.Equal(decision, rendered.Decision);
        Assert.Equal(Feedback, rendered.Feedback);
        Assert.Equal(expected.ToString(), rendered.AppliedTo);
        Assert.Contains(Feedback, board.Read(Swimlanes.Label(expected)), StringComparison.Ordinal);

        // Where the loop put the work item to apply this one is said on the board, not only
        // kept in an attribute. It is the whole of what the decision did, so an approval the
        // loop could not carry out stays readable as such after the page is read again.
        Assert.Contains(
            $"applied to {Swimlanes.Label(expected)}",
            board.Read(Swimlanes.Label(expected)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_for_changes_returns_the_work_item_to_the_build_and_the_next_round_costs_another_round()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode()
            .Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.")
            .Yielding(RoundOutcome.Produced, "src/Index.cs +14 -3", "Second attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = await InReview(host);

        var before = host.Store.Get(workItem.Id)!;
        Assert.Equal(1, before.RoundCount);

        using var response = await Board.DecideAsync(host.Board, workItem.Id, "request-changes", Feedback);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Straight back into the build, which is the same lane a work item is accepted
        // into from Backlog — the next round starts from there and not from a state of
        // its own.
        Assert.Equal(Swimlane.Frontier, host.Store.Get(workItem.Id)!.Swimlane);
        var board = await Board.ReadAsync(host.Board);
        Assert.Contains(workItem.Id.ToString(), board.Swimlane("Frontier"), StringComparison.Ordinal);
        var decided = Assert.Single(board.DecisionsOn(workItem.Id));
        Assert.Equal(Feedback, decided.Feedback);
        Assert.Equal("Frontier", decided.AppliedTo);

        // The build runs, and the round it runs costs the work item another round. The
        // count is of rounds run, so it is the round that comes back that moves it.
        await host.Settle();

        var after = host.Store.Get(workItem.Id)!;
        Assert.Equal(Swimlane.Review, after.Swimlane);
        Assert.Equal(before.RoundCount + 1, after.RoundCount);
        Assert.Equal([1, 2], host.Store.Rounds(workItem.Id).Select(round => round.RoundNumber));
        Assert.Equal(2, agent.AskedFor.Count);
    }

    [Fact]
    public async Task The_next_round_is_handed_the_reviewers_words_as_its_brief()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode()
            .Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.")
            .Yielding(RoundOutcome.Produced, "src/Index.cs +14 -3", "Second attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = await InReview(host);

        using (await Board.DecideAsync(host.Board, workItem.Id, "request-changes", Feedback))
        {
        }

        await host.Settle();

        // A first round has nobody's words to be briefed with, and a round after a
        // request for changes is briefed with exactly what the reviewer wrote — the
        // reviewer's words, not a verdict and not a paraphrase of them.
        Assert.Equal([string.Empty, Feedback], agent.AskedFor.Select(round => round.Feedback));
    }

    [Fact]
    public async Task A_later_request_for_changes_briefs_the_next_round_with_the_later_words()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode()
            .Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.")
            .Yielding(RoundOutcome.Produced, "src/Index.cs +14 -3", "Second attempt.")
            .Yielding(RoundOutcome.Produced, "src/Index.cs +15 -3", "Third attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = await InReview(host);

        using (await Board.DecideAsync(host.Board, workItem.Id, "request-changes", Feedback))
        {
        }

        await host.Settle();

        using (await Board.DecideAsync(host.Board, workItem.Id, "request-changes", LaterFeedback))
        {
        }

        await host.Settle();

        // The most recent thing the reviewer said is the brief, and both sets of words
        // are still on the record. This is also where the loop's being unbounded is
        // visible: a third round runs because nothing has yet said it should not. The
        // round ceiling is the exhaustion ticket's, and it is not here.
        Assert.Equal([string.Empty, Feedback, LaterFeedback], agent.AskedFor.Select(round => round.Feedback));
        Assert.Equal(Swimlane.Review, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Equal(3, host.Store.Get(workItem.Id)!.RoundCount);
        Assert.Equal([Feedback, LaterFeedback], host.Store.Decisions(workItem.Id).Select(decision => decision.Feedback));
    }

    [Fact]
    public async Task No_decision_moves_a_rejected_work_item_back()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode()
            .Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.")
            .Yielding(RoundOutcome.Produced, "src/Review.cs +4 -0", "Second attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var (rejected, other) = await TwoInReview(host);

        using (await Board.DecideAsync(host.Board, rejected.Id, "reject"))
        {
        }

        Assert.Equal(Swimlane.Rejected, host.Store.Get(rejected.Id)!.Swimlane);

        // And the board does not offer a decision on a work item it would refuse one for:
        // the form is rendered in Review and nowhere else, so a rejected work item shows
        // the decision that rejected it and nothing to press.
        var refused = await Board.ReadAsync(host.Board);
        Assert.Equal(string.Empty, refused.DecisionFormFor(rejected.Id));
        Assert.NotEqual(string.Empty, refused.DecisionFormFor(other.Id));

        // Every one of the three, against a work item a reviewer has already declined.
        // Rejected is final: a human has said something conclusive about the change, and
        // no decision is a way back from it. The other work item is in Review throughout
        // so that the board is rendering a decision form at all — without it this post
        // would be refused for having no token rather than for what it is asking.
        foreach (var decision in new[] { "approve", "request-changes", "reject" })
        {
            using var response = await Board.DecideAsync(
                host.Board,
                rejected.Id,
                decision,
                decision == "request-changes" ? Feedback : null);

            var board = await Board.ReadAsync(response);
            Assert.Equal(
                "nexus#42 is in Rejected, not in Review, so there is nothing to decide about it",
                board.Refusal());
            Assert.Equal(Swimlane.Rejected, host.Store.Get(rejected.Id)!.Swimlane);
            Assert.Equal(Swimlane.Review, host.Store.Get(other.Id)!.Swimlane);
            Assert.Single(host.Store.Rounds(rejected.Id));
            Assert.Single(host.Store.Decisions(rejected.Id));
        }
    }

    [Fact]
    public async Task A_request_for_changes_with_nothing_to_say_is_refused()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = await InReview(host);

        // Empty and blank are the same absence of words, and both are refused. The next
        // round's brief is the reviewer's reasons; there are none here, and a factory
        // that sent the agent round again with nothing — or with a message it made up —
        // would be deciding what the reviewer said.
        foreach (var silent in new[] { "", "   " })
        {
            using var response = await Board.DecideAsync(host.Board, workItem.Id, "request-changes", silent);

            var board = await Board.ReadAsync(response);
            Assert.Equal(
                "requesting changes needs the reviewer's reasons: they are the next round's brief, "
                    + "and there is nothing here to hand it",
                board.Refusal());

            // Refused means refused: the work item is still the reviewer's to decide, no
            // decision was recorded, and no round was started.
            Assert.Equal(Swimlane.Review, host.Store.Get(workItem.Id)!.Swimlane);
            Assert.Empty(host.Store.Decisions(workItem.Id));
            Assert.Single(agent.AskedFor);
        }

        // Approve and Reject need no words, because neither of them briefs a round.
        using (await Board.DecideAsync(host.Board, workItem.Id, "reject"))
        {
        }

        Assert.Equal(Swimlane.Rejected, host.Store.Get(workItem.Id)!.Swimlane);
    }

    [Theory]
    [InlineData("shelve")]
    [InlineData("merge")]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_decision_the_board_does_not_offer_is_refused(string? decision)
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = await InReview(host);

        using var response = await Board.PostByHandAsync(host.Board, workItem.Id, decision, Feedback);

        var board = await Board.ReadAsync(response);
        Assert.Equal(
            "That is not one of the three decisions. A work item in Review is approved, "
                + "sent back for changes, or rejected, and there is nothing else to decide.",
            board.Refusal());
        Assert.Equal(Swimlane.Review, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Empty(host.Store.Decisions(workItem.Id));
        Assert.Single(agent.AskedFor);
    }

    [Fact]
    public async Task The_board_is_the_only_way_a_human_can_change_anything()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode()
            .Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.")
            .Yielding(RoundOutcome.Produced, "src/Review.cs +4 -0", "Second attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var (rejected, waiting) = await TwoInReview(host);

        // The process serves a known, closed set of endpoints, and every one of them that
        // can change anything is the board page. Anything a human — or anything at all —
        // can reach arrives through one of them, because there is
        // Nothing else addressable beside the two pages and the JSON surface: the board,
        // the projects surface (#33), which writes project files and secrets and moves no
        // work item, and the admin app's `/api` group — the board read (#43), the review
        // workspace's open (#47), which stands a container up and moves no work item, the
        // projects read and writes (#48), and the credentials surface (#49), whose `GET`
        // lists names and presence, whose `PUT` writes a secret, and whose `DELETE` removes
        // one — none of which moves a work item. A Razor page's route pattern carries no raw text of
        // its own, so the endpoint is named rather than spelled as a URL. A middleware that
        // wrote without going through routing would not be on this list, and there is none;
        // what the list does prove is that nothing else is reachable. The raw pattern is the
        // pages-root-relative template, which is why the projects page's is "Projects" and
        // the board's is "/Index"; the JSON endpoints are minimal-API routes and carry their
        // own absolute templates, and `PUT` and `DELETE` on one path collapse to one entry
        // because the pattern is what is listed.
        Assert.Equal(
            [
                "/Index",
                "/api/board",
                "/api/credentials",
                "/api/credentials/{name}",
                "/api/projects",
                "/api/projects/{name}",
                "/api/work-items/{id}/workspace",
                "Projects",
            ],
            host.Routes());

        // And the board itself has three writes: the reviewer's decision — the only one
        // that finishes built work — the review workspace's open (#35), which moves
        // nothing, and the Backlog gate's build (#40), whose move is the loop's own
        // acceptance. Anything else that changed a work item would have to appear here to
        // be reachable at all.
        Assert.Equal(["OnGet", "OnPostBuildAsync", "OnPostDecision", "OnPostOpenWorkspaceAsync"], host.BoardHandlers());

        // And the page does not move work items itself. Which swimlane a decision means
        // is the loop's policy, and a page that moved work items would be a second state
        // machine that could disagree with the loop about the same work item. This is a
        // check of the page's own source rather than of behaviour, and the honest limit
        // of it is that it would not catch a differently spelled way to move one.
        var page = ReadTheFile("Pages", "Index.cshtml.cs");
        Assert.DoesNotContain(".Move(", page, StringComparison.Ordinal);
        Assert.DoesNotContain(".ApplyDecision(", page, StringComparison.Ordinal);
        Assert.Contains("RecordDecision(", page, StringComparison.Ordinal);
        Assert.Contains("StepAsync()", page, StringComparison.Ordinal);

        // And it does not merge anything itself either. Approve means merged, so the merge
        // is the loop's to ask the GitHub seam for; a page that merged would be a second
        // thing shipping changes, outside the loop whose answer is what Done means.
        Assert.DoesNotContain("MergeAsync(", page, StringComparison.Ordinal);

        // So the three decisions are the whole of it: exactly the three the board offers
        // a reviewer in Review, and the store refuses a decision about anything not in
        // Review — which is what stops an escalation or a done work item being moved by
        // hand through the same form. A rejected one is refused by all three, and the
        // work item stays rejected with the one decision that put it there. The other
        // work item stays in Review throughout, so the board is still rendering a form
        // to post from.
        var board = await Board.ReadAsync(host.Board);
        Assert.Equal(
            ["approve", "request-changes", "reject"],
            Board.ValuesOf(board.DecisionFormFor(rejected.Id), "data-decision"));

        using (await Board.DecideAsync(host.Board, rejected.Id, "reject"))
        {
        }

        foreach (var decision in new[] { "approve", "request-changes", "reject" })
        {
            using var response = await Board.PostByHandAsync(host.Board, rejected.Id, decision, Feedback);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(Swimlane.Rejected, host.Store.Get(rejected.Id)!.Swimlane);
        }

        Assert.Equal(Swimlane.Review, host.Store.Get(waiting.Id)!.Swimlane);
        Assert.Single(host.Store.Decisions(rejected.Id));
    }

    [Fact]
    public async Task A_decision_posted_without_the_boards_own_token_is_refused()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        var workItem = await InReview(host);

        // The board's form carries a token and the endpoint requires it, so a post made
        // without one — a curl, another page, anything that is not this page's form — is
        // not a decision at all. The write path is the reviewer's form and nothing else.
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["workItem"] = workItem.Id.ToString(),
            ["decision"] = "approve",
            ["feedback"] = Feedback,
        });

        using var response = await host.Board.PostAsync("/?handler=Decision", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(Swimlane.Review, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Empty(host.Store.Decisions(workItem.Id));
    }

    /// <summary>The board's own page, read from disk, for the checks that are about source.</summary>
    private static string ReadTheFile(params string[] beneath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine([directory.FullName, "src", "agent-factory", .. beneath]);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        throw new DirectoryNotFoundException(
            $"no {Path.Combine(beneath)} found above {AppContext.BaseDirectory}");
    }

    /// <summary>A work item with a round behind it, sitting in Review where a reviewer can decide.</summary>
    private static async Task<WorkItem> InReview(FactoryHost host, int issueNumber = 42)
    {
        var workItem = host.Store
            .Intake("nexus", RepoUrl, issueNumber, "A work item, end to end", IssueBody, "main")
            .WorkItem;

        await host.PromoteAsync(workItem.Id);

        await host.Settle();

        Assert.Equal(Swimlane.Review, host.Store.Get(workItem.Id)!.Swimlane);
        return workItem;
    }

    /// <summary>Two work items with a round behind each, both waiting on a reviewer.</summary>
    private static async Task<(WorkItem First, WorkItem Second)> TwoInReview(FactoryHost host)
    {
        var first = await InReview(host, 42);
        var second = await InReview(host, 43);
        return (first, second);
    }
}
