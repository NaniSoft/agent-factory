namespace AgentFactory.GitHub;

/// <summary>
/// What a change is called on the remote, and what its pull request says. Both are derived
/// from the work item, because the work item is the factory's record of the issue and the
/// change answers exactly that issue.
/// </summary>
/// <remarks>
/// <para>
/// The branch is <c>agent-factory/&lt;issue-number&gt;-&lt;slug&gt;</c>, as the spec names
/// it. The number is the work item's issue number and the slug is its title, so the two
/// are related by reading the branch name and nothing else has to be looked up — which is
/// the point of the naming (story 51).
/// </para>
/// <para>
/// It is also what makes the push idempotent, and that turns out to matter more than the
/// naming does. Two calls about one work item derive the same branch, so the second call
/// finds the first one's work on the remote and finishes it rather than starting a second
/// one. A branch named after a commit, a timestamp or anything else unique per attempt
/// would make every attempt a new branch and every one of them a new pull request.
/// </para>
/// </remarks>
public static class BranchName
{
    /// <summary>The namespace every change the factory ships lives under.</summary>
    public const string Prefix = "agent-factory";

    /// <summary>
    /// How much of an issue's title goes into the branch. Long enough to be recognisable,
    /// short enough that the whole ref stays comfortably inside git's and GitHub's own
    /// limits whatever the title says.
    /// </summary>
    public const int MaxSlugLength = 60;

    /// <summary>The branch a change for this issue is pushed to.</summary>
    public static string For(int issueNumber, string? issueTitle) =>
        $"{Prefix}/{issueNumber}-{Slug(issueTitle)}";

    /// <summary>
    /// An issue's title, reduced to something git will accept as a ref component and a
    /// person will recognise.
    /// </summary>
    /// <remarks>
    /// Everything outside <c>a-z 0-9</c> becomes a hyphen, runs of hyphens collapse, and
    /// the ends are trimmed — so "Fix: the thing  (again!)" reads as
    /// "fix-the-thing-again". A title with nothing sluggable in it at all — an emoji, or
    /// a title in a script this deliberately does not transliterate — gets a fixed word
    /// rather than an empty component, because <c>agent-factory/42-</c> is a ref git will
    /// accept and nobody can read. Non-ASCII is dropped rather than transliterated on
    /// purpose: a guessed transliteration is a name nobody can match back to the issue.
    /// </remarks>
    public static string Slug(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "change";
        }

        var slug = new System.Text.StringBuilder(MaxSlugLength);
        var lastWasHyphen = false;

        foreach (var character in title.ToLowerInvariant())
        {
            if (slug.Length >= MaxSlugLength)
            {
                break;
            }

            var keep = character is >= 'a' and <= 'z' || character is >= '0' and <= '9';
            if (keep)
            {
                slug.Append(character);
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen)
            {
                slug.Append('-');
                lastWasHyphen = true;
            }
        }

        // A cut that landed inside a word is still a word, but a trailing hyphen is not a
        // name; and an empty result means the title had nothing sluggable in it.
        var trimmed = slug.ToString().Trim('-');
        return trimmed.Length == 0 ? "change" : trimmed;
    }
}
