namespace AgentFactory.Polling;

using AgentFactory.Failures;
using AgentFactory.Projects;

/// <summary>
/// What the factory last did about one project's open issues, in the three words a reviewer
/// needs to tell apart.
/// </summary>
/// <remarks>
/// <para>
/// These are the same three the board draws for a round's diff — <c>unavailable</c>,
/// <c>empty</c> and nothing-on-record — and they are here for the same reason. An empty
/// Backlog lane is three different facts: the repository was read and had nothing open, or
/// the factory has never read it, or the factory tried and was refused. One absence
/// standing for three of them is the defect #16 found, because the board said "Serving 2
/// projects" above an empty Backlog and not one poll had ever succeeded.
/// </para>
/// <para>
/// It is intake's own state rather than a work item's, and it is deliberately not a work
/// item: a repository that cannot be read has produced no work item to hang a fact about,
/// which is the same reason intake does not escalate (ADR-0008). So this is a project's,
/// keyed by the project's name, and it is what the board renders above the lanes rather
/// than on a card.
/// </para>
/// </remarks>
public enum IntakeStatus
{
    /// <summary>
    /// The factory has not read this repository since it started. Whether that is because
    /// no pass has come round yet or because the factory has only just started is not
    /// something this says, and it does not need to: either way the factory knows nothing
    /// about the repository yet.
    /// </summary>
    NeverPolled,

    /// <summary>
    /// The repository was read and the read succeeded — which includes a read that found
    /// nothing open. "Polled" is not a claim that there is work: the count of open issues
    /// is beside it, because "polled and found nothing" is a real answer and "the poll
    /// never happened" is not one.
    /// </summary>
    Polled,

    /// <summary>
    /// The repository could not be read the last time the factory tried. What it failed
    /// as is beside it: a transient failure is asked again on a backoff, and a permanent
    /// one is not asked again at all.
    /// </summary>
    Failing,
}

/// <summary>
/// One project's intake, as the board shows it. A snapshot rather than a record of record:
/// it is in memory, it is gone on a restart, and it is not written to the store — a poll
/// is a read, and nothing about it is a fact a reviewer needs to survive a restart
/// (ADR-0009).
/// </summary>
/// <param name="Project">The project's name, as its project file spells it.</param>
/// <param name="RepoUrl">The repository, so a reviewer can see which one this is about.</param>
/// <param name="Status">Which of the three things has happened.</param>
/// <param name="OpenIssues">
/// How many open issues the last successful read found, or zero when there has not been
/// one. Never read for anything but <see cref="IntakeStatus.Polled"/>: the board renders
/// it only then, because a number beside "could not be read" would be a number about a
/// read that did not happen.
/// </param>
/// <param name="Failure">How the last turn failed, when it failed.</param>
/// <param name="Because">What the last turn said when it failed, in its own words.</param>
/// <param name="AtUtc">
/// When the last turn finished, either way. A failure a reviewer is looking at is one they
/// need to be able to date.
/// </param>
/// <param name="Failures">
/// How many times in a row this project has failed. A run of failures, not failures ever:
/// a success clears it, so a project that answers again is not counted against itself for
/// ever.
/// </param>
/// <param name="AgainAfterUtc">
/// When this project will be read again, or null when it will not be — which is what a
/// permanent failure means, and is the only case a null is allowed to be about. A read that
/// succeeded has no wait on it (intake's own cadence is the wait) and neither has one that
/// has never been taken.
/// </param>
public sealed record IntakeRecord(
    string Project,
    string RepoUrl,
    IntakeStatus Status,
    int OpenIssues,
    FailureClass? Failure,
    string? Because,
    DateTimeOffset? AtUtc,
    int Failures,
    DateTimeOffset? AgainAfterUtc)
{
    /// <summary>
    /// A project nothing has been read for yet. What the board shows before the rotation
    /// has come round to a project, and the honest answer rather than an absent one: an
    /// intake section with no row for a project says nothing, and a reviewer reading
    /// nothing concludes the project has nothing open.
    /// </summary>
    public static IntakeRecord NeverRead(Project project) => new(
        project.Name,
        project.RepoUrl,
        IntakeStatus.NeverPolled,
        OpenIssues: 0,
        Failure: null,
        Because: null,
        AtUtc: null,
        Failures: 0,
        AgainAfterUtc: null);
}
