namespace AgentFactory.GitHub;

using AgentFactory.WorkItems;

/// <summary>
/// What the pull request is called and what it says, both composed from the work item.
/// </summary>
/// <remarks>
/// <para>
/// The title is the issue's own title, unchanged. That is the strongest statement of
/// "the pull request carries the work item's identity" there is: the two things a reader
/// compares — the issue and the change — are spelled identically, and a prefix would only
/// make them differ. Who opened it is on the body's first line and in the branch name,
/// which are the two places a maintainer looks when they want to know whether a machine
/// did this.
/// </para>
/// <para>
/// <strong>The issue is linked, not closed.</strong> The body says <c>Refs #42</c> and
/// never a closing keyword, so merging this pull request creates a link and closes
/// nothing. That is a decision, and the argument is in the body itself: whether the
/// factory considers an issue finished is the board's answer about a work item, and a
/// merge is a repository operation that does not carry that judgement with it. The two
/// ways this factory can merge without a human having read the change — the 48-hour
/// threshold, and a retry of a merge that failed — would both close somebody's issue as a
/// side effect, and saying "this is done" to everyone watching that issue on the strength
/// of a change nobody read is a second claim the merge has not earned. Nothing is lost by
/// leaving the issue open: intake is idempotent on
/// <c>(repo_url, issue_number)</c>, so a still-open issue is found again and changes
/// nothing about the work item that is already Done.
/// </para>
/// <para>
/// The words that would close an issue are avoided throughout, not just at the reference,
/// so that a later edit adding a sentence cannot introduce one by accident. A test holds
/// the rendered body against GitHub's own closing keywords.
/// </para>
/// </remarks>
public static class PullRequestText
{
    /// <summary>What the pull request is called: the issue's own title.</summary>
    public static string Title(WorkItem workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        return workItem.IssueTitle.Trim() is { Length: > 0 } title
            ? title
            : $"The change for issue #{workItem.IssueNumber}";
    }

    /// <summary>
    /// What the pull request says. Factual about what happened and about what is in the
    /// branch, and deliberately silent about whether a human read it: the factory can
    /// merge a change nobody reviewed, and a body that claimed otherwise would be a lie
    /// in somebody's repository.
    /// </summary>
    public static string Body(WorkItem workItem, string branch)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        return $"""
            Refs #{workItem.IssueNumber}

            Opened by the agent factory, from the host. The change was built in a worker
            container, which committed it and stopped there; the host pushed the branch
            and opened this pull request (ADR-0006).

            The branch `{branch}` is the round's own commit and nothing else. No follow-up
            commits, no merge-time edits, and nothing the container could write has been
            added to it on the way here.

            Linked rather than closed on purpose. Whether the issue is finished is the
            factory's board's answer about a work item, and a merge is not that answer.
            """;
    }
}
