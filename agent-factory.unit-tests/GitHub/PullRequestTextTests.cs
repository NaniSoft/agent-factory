namespace AgentFactory.UnitTests.GitHub;

using AgentFactory.Application.GitHub;
using AgentFactory.UnitTests.Boundary;
using System.Text.RegularExpressions;

/// <summary>
/// What the pull request is called and what it says, at the layer where the rule is decided.
/// </summary>
/// <remarks>
/// The closing-keyword rule is already asserted in `GitHubReadTests` against GitHub's own list.
/// This adds the two halves that file leaves: the title's fallback when an issue has none, and the
/// rule read as a shape rather than a list — a closing keyword next to <em>any</em> issue number,
/// which is what a later sentence introducing one by accident would look like.
/// </remarks>
public class PullRequestTextTests
{
    /// <summary>
    /// GitHub closes an issue when a pull request references it with one of its own keywords. The
    /// list is GitHub's; the pattern is written here so a new sentence that introduced one next to
    /// any number fails, not only the number this test happened to pick.
    /// </summary>
    private static readonly Regex Closing = new(
        @"\b(close[sd]?|fix(es|ed)?|resolve[sd]?)\b\s*:?\s*#\d+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void The_title_is_the_issues_own_and_falls_back_when_it_has_none()
    {
        Assert.Equal(
            "The poller stops when a project is paced",
            PullRequestText.Title(AWorkItem.For(title: "The poller stops when a project is paced")));

        // A blank title is not an empty pull request: it is named by the issue it answers, so a
        // reader still knows what they are looking at.
        Assert.Equal("The change for issue #42", PullRequestText.Title(AWorkItem.For(title: "   ")));
    }

    [Fact]
    public void The_body_links_the_issue_and_never_closes_it()
    {
        // The issue is linked, not closed: whether the factory considers an issue finished is the
        // board's answer about a work item, and a merge is a repository operation that does not
        // carry that judgement with it. The two ways this factory can merge without a human having
        // read the change would both close somebody's issue as a side effect.
        foreach (var title in new[]
        {
            "The poller stops when a project is paced",
            "Fix the thing",
            "Close the loop",
            "Resolved at last",
        })
        {
            var workItem = AWorkItem.For(title: title);
            var body = PullRequestText.Body(workItem, BranchName.For(42, title));

            Assert.StartsWith("Refs #42", body, StringComparison.Ordinal);
            Assert.DoesNotMatch(Closing, body);
        }
    }

    [Fact]
    public void The_body_names_the_branch_so_a_reader_can_check_the_change_against_it()
    {
        var workItem = AWorkItem.For();
        var branch = BranchName.For(42, workItem.IssueTitle);
        var body = PullRequestText.Body(workItem, branch);

        Assert.Contains(branch, body, StringComparison.Ordinal);

        // And it is honest about what the factory can do unattended rather than claiming a human
        // read the change: it says what it did and leaves the judgement to the board.
        Assert.Contains("Linked rather than closed", body, StringComparison.Ordinal);
    }
}
