namespace AgentFactory.Observability;

using Microsoft.Extensions.Logging;

/// <summary>
/// The identity a record carries because of where it was written, when the thing it is
/// about is a project rather than a work item.
/// </summary>
/// <remarks>
/// <para>
/// This exists because intake has records that <see cref="WorkItemScope"/> cannot honestly
/// carry. A project whose repository could not be read has produced no work item, so there
/// is no work item id, no issue number and no round to name: a failure at intake is a fact
/// about a project, and it has to be findable from that and from nothing else. Putting the
/// project's name on such a record under <c>WorkItemScope.ProjectKey</c> would mean a
/// field called "the work item's project" carrying a project with no work item, and filling
/// the other three keys would mean inventing a work item id, an issue number and a round
/// number for a work item that does not exist.
/// </para>
/// <para>
/// <strong>So the work item's vocabulary is untouched, and this is a second list rather
/// than a fifth key in the first one.</strong> The four keys in <see cref="WorkItemScope"/>
/// are work-item-shaped — the work item, its project, its issue, its round — and a
/// project-level record belongs to none of those except the project, which is the one fact
/// it has. Widening that list to carry a scope that is not a work item would make every
/// existing assertion about it mean two things, and the ten call sites
/// <c>PolicyTests</c> pins would still be right while the vocabulary they were pinned
/// against was not. So the vocabulary stays as it was and this is its own class with its
/// own key and its own pinned list, and <c>PolicyTests</c> asserts both.
/// </para>
/// <para>
/// <strong>The key is prefixed for the reason the work item's are.</strong> A record's own
/// message usually names the project — the poller's intake failure says <c>{Project}</c> in
/// the sentence a human reads — and a JSON sink that merged a scope key and a message key
/// of the same name would emit the same field twice, with the message's value winning. The
/// scope is the <em>machine</em> identity and the message is the <em>human</em> one, and
/// they are named so they cannot collide.
/// </para>
/// </remarks>
public static class ProjectScope
{
    /// <summary>The project's name, as its project file spells it.</summary>
    public const string ProjectKey = "FactoryProject";

    /// <summary>
    /// Every key a project scope can carry, in the order it is put on a record. A test
    /// asserts this is the whole set, so that a second kind of ambient identity cannot be
    /// added to this either without somebody meeting it.
    /// </summary>
    public static readonly IReadOnlyList<string> Keys = [ProjectKey];

    /// <summary>
    /// Puts the project's name on every record written until the returned scope is
    /// disposed. Ambient rather than passed down, for the reason
    /// <see cref="WorkItemScope"/> is: an identity threaded through a call chain is one
    /// somebody can forget to pass, and the record that forgets it is exactly the record
    /// that cannot be attributed.
    /// </summary>
    /// <remarks>
    /// The project is the whole of the identity here. There is no issue to narrow it to —
    /// a turn at a project's repository is one read of that repository, and it has failed
    /// before there was any issue in it — and no round, because no round has started.
    /// </remarks>
    public static IDisposable? ForProject(this ILogger logger, string project)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);

        return logger.BeginScope(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [ProjectKey] = project,
        });
    }
}
