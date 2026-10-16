namespace AgentFactory.UnitTests.GitHub;

using AgentFactory.Application.GitHub;
using System.Text.RegularExpressions;

/// <summary>
/// What a change is called on the remote, at the layer where the rule is decided.
/// </summary>
/// <remarks>
/// The slug's own examples — "Fix: the thing (again!)" and the long-title bound — are already
/// asserted in `GitHubReadTests`, so they are not repeated here. What this file adds is the
/// property one layer up: that whatever an issue's title says, the whole branch is a git ref, an
/// HTTP path segment and a pull-request body all at once, and that it is <em>deterministic</em> —
/// because that determinism is not a nicety, it is the idempotency the whole merge depends on.
/// </remarks>
public class BranchNameTests
{
    /// <summary>What a git ref component, a URL path segment and a branch are all allowed to be.</summary>
    private static readonly Regex Shape = new(@"^agent-factory/[0-9]+-[a-z0-9-]+$", RegexOptions.Compiled);

    private static readonly Regex Sluggable = new(@"^[a-z0-9-]+$", RegexOptions.Compiled);

    [Fact]
    public void A_branch_is_a_ref_a_url_segment_and_a_name_whatever_the_title_is()
    {
        // A title is a stranger's words. It reaches a git ref, a path segment and a body, so the
        // hostile set below is the set that would break one of those: a path traversal, a git ref
        // operator, a URL metacharacter, an escape, a control character and a script this
        // deliberately does not transliterate.
        foreach (var title in new[]
        {
            "../../etc/passwd",
            "a/b\\c:d*e?f[g]h",
            "Fix: the thing  (again!)",
            "~^:?*[\\",
            "\t\n\r leading and control",
            "修复轮次超时",
            "emoji 🚀✨ only",
            "  ",
            "!!!",
            new string('x', 400),
        })
        {
            var branch = BranchName.For(42, title);

            Assert.Matches(Shape, branch);
            Assert.DoesNotContain("..", branch, StringComparison.Ordinal);
            Assert.DoesNotContain("//", branch, StringComparison.Ordinal);
            Assert.DoesNotContain(" ", branch, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_same_issue_and_title_always_derive_the_same_branch()
    {
        // This is the idempotency, stated where the branch is decided. A branch named after a
        // commit, a timestamp or anything else unique per attempt would make every attempt a new
        // branch and every one of them a new pull request.
        var first = BranchName.For(42, "The poller stops when a project is paced");
        var second = BranchName.For(42, "The poller stops when a project is paced");

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_slug_never_ends_in_a_hyphen_even_when_the_cut_lands_on_one()
    {
        // The truncation happens mid-loop, so a title whose 60th character is a space would leave a
        // trailing hyphen — which is a legal git ref and a name nobody wants.
        var cut = BranchName.Slug(new string('a', 59) + " tail");

        Assert.Equal(59, cut.Length);
        Assert.False(cut.EndsWith("-", StringComparison.Ordinal));
        Assert.Matches(Sluggable, cut);
    }

    [Fact]
    public void An_unsluggable_title_gets_a_word_rather_than_an_empty_ref_component()
    {
        // `agent-factory/42-` is a ref git will accept and nobody can read, so the empty component
        // is replaced rather than left. Every one of these must be the same fixed word.
        foreach (var title in new string?[] { "", null, "   ", "!!!", "🚀✨", "修复轮次超时" })
        {
            Assert.Equal("change", BranchName.Slug(title));
            Assert.Equal("agent-factory/42-change", BranchName.For(42, title));
        }
    }
}
