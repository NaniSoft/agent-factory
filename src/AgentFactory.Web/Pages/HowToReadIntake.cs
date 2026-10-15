namespace AgentFactory.Pages;

using AgentFactory.Failures;
using AgentFactory.Polling;

/// <summary>
/// What the board says about intake, and why it says it that way. The whole of the
/// judgement; the view renders what this says and decides nothing.
/// </summary>
/// <remarks>
/// <para>
/// Intake is the one surface a reviewer trusts first — everything else on the board is
/// downstream of it — and it is the one that has had no states of its own. An empty Backlog
/// lane used to be one rendered thing standing for three facts: the repository was read
/// and had nothing open, the factory has never read it, or the factory tried and was
/// refused. #16 is the run that showed what that costs: "Serving 2 projects" above an
/// empty Backlog, and not one poll had ever succeeded, because the GitHub client was
/// refused every request it made.
/// </para>
/// <para>
/// So this is the same treatment a round's diff already gets, and for the same reason. The
/// board distinguishes a diff that is empty from one that could not be generated from one
/// with no record at all, each with its own rendered shape and its own test; intake now
/// distinguishes never-polled, polled and failing the same way. The states are named in the
/// markup (<c>data-intake-state</c>) as well as in the prose, so a test asserts on what the
/// board claims rather than on a sentence that could be reworded to say the same thing.
/// </para>
/// <para>
/// And the section is always rendered when the factory serves a project, healthy or not,
/// because a fault that is only rendered when something else is wrong is a fault nobody
/// sees. A healthy factory says intake is working, so the absence of a complaint is itself
/// a stated fact rather than an absence — the same argument the board already makes about
/// the container budget.
/// </para>
/// </remarks>
internal static class HowToReadIntake
{
    /// <summary>
    /// The state as the board names it. A slug rather than the enum's own name because a
    /// state is read by a person in the markup and by a test in the same breath, and
    /// <c>NeverPolled</c> lowercased is not a word.
    /// </summary>
    public static string Slug(IntakeStatus status) => status switch
    {
        IntakeStatus.NeverPolled => "never-polled",
        IntakeStatus.Polled => "polled",
        IntakeStatus.Failing => "failing",

        // A cast value rather than a member the factory defines. Thrown rather than
        // rendered as something, because a state nobody has argued for is not a state to
        // hand a reviewer as though it meant something.
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status, "a state the board cannot name is a state nobody can be told about"),
    };

    /// <summary>
    /// What each project's intake is as the board shows it, in the rotation order the
    /// poller holds them in.
    /// </summary>
    public static IReadOnlyList<IntakeOnTheBoard> Each(IReadOnlyList<IntakeRecord> intake) =>
        [.. intake.Select(project => new IntakeOnTheBoard(
            project.Project,
            project.RepoUrl,
            project.Status,
            project.Status == IntakeStatus.Polled ? project.OpenIssues : null,
            project.Failure,
            project.Because,
            project.AtUtc,
            project.Failures,
            project.AgainAfterUtc))];

    /// <summary>
    /// The state of the whole factory, which is the worst of the projects' — in that order
    /// of how bad it is for a reviewer looking at an empty lane.
    /// </summary>
    /// <remarks>
    /// "Never polled" is worse than "polled" for this purpose even though neither is a
    /// fault, because the factory has established nothing at all about the repository: a
    /// reader who sees a project that has been polled and one that has not cannot tell
    /// what the factory knows, and "nothing open" is an answer only the first of them can
    /// give. A failure beats both because the factory has an answer and the answer is no.
    /// </remarks>
    public static IntakeStatus WorstOf(IReadOnlyList<IntakeOnTheBoard> intake)
    {
        if (intake.Any(project => project.Status == IntakeStatus.Failing))
        {
            return IntakeStatus.Failing;
        }

        return intake.Any(project => project.Status == IntakeStatus.NeverPolled)
            ? IntakeStatus.NeverPolled
            : IntakeStatus.Polled;
    }

    /// <summary>
    /// The one line above the per-project rows: whether intake is working, and — when it is
    /// not — that an empty Backlog does not mean there is nothing to do.
    /// </summary>
    /// <remarks>
    /// This sentence is the fix for the defect rather than the rows below it. A fault named
    /// in a list a reviewer has to know to look at is a fault that reads as an empty lane,
    /// and the failure #16 was is precisely that a reviewer cannot distinguish "polled,
    /// nothing open" from "has never polled successfully". So the whole-board line says it
    /// in words, on the board, whenever it is true.
    /// </remarks>
    public static string Summarise(IReadOnlyList<IntakeOnTheBoard> intake)
    {
        if (intake.Count == 0)
        {
            return "The factory is serving no projects, so there is nothing to poll.";
        }

        var failing = intake.Count(project => project.Status == IntakeStatus.Failing);
        var never = intake.Count(project => project.Status == IntakeStatus.NeverPolled);

        if (failing > 0)
        {
            return $"{failing} of {intake.Count} project(s) cannot be read. An empty Backlog does not mean there is "
                + "nothing to do while intake is broken.";
        }

        if (never > 0)
        {
            return $"{never} of {intake.Count} project(s) have not been polled yet. An empty Backlog does not mean there "
                + "is nothing to do until they have been.";
        }

        return "Intake is reading every project the factory serves.";
    }
}

/// <summary>
/// One project's intake as the board renders it: the state, what was found or what was
/// refused, and when this project will be read again — which is a different answer for each
/// of the three states and the one a reviewer cannot work out for themselves.
/// </summary>
/// <param name="Project">The project's name, as its project file spells it.</param>
/// <param name="RepoUrl">The repository, so the row says which one it is about.</param>
/// <param name="Status">Which of the three things has happened.</param>
/// <param name="OpenIssues">
/// How many open issues the last successful read found, and null for the two states where
/// there has not been one. Null rather than zero, because zero is a real answer and
/// rendering it beside a read that did not happen would be inventing one.
/// </param>
/// <param name="Failure">How the last turn failed, when it failed.</param>
/// <param name="Because">What the last turn said when it failed, in its own words.</param>
/// <param name="AtUtc">When the last turn finished, either way.</param>
/// <param name="Failures">How many times in a row this project has failed.</param>
/// <param name="AgainAfterUtc">When this project will next be read, when it will be.</param>
public sealed record IntakeOnTheBoard(
    string Project,
    string RepoUrl,
    IntakeStatus Status,
    int? OpenIssues,
    FailureClass? Failure,
    string? Because,
    DateTimeOffset? AtUtc,
    int Failures,
    DateTimeOffset? AgainAfterUtc)
{
    /// <summary>
    /// Whether this project will be read again, and when — one word for each answer
    /// because "later" and "never" and "on the next pass" are three very different states
    /// and a project left failing for ever is the one a reviewer has to be able to see
    /// without reading a sentence.
    /// </summary>
    /// <remarks>
    /// A permanent failure is <c>never</c> for this process, and the reason is written on
    /// the row rather than left to a reader: project files and the environment are read at
    /// start, so nothing about a permanent failure changes while the factory runs.
    /// </remarks>
    public string Again => AgainAfterUtc is { } again
        ? again.ToString("O")
        : Status == IntakeStatus.Failing ? "never" : "next-pass";

    /// <summary>
    /// The row's sentence, in the reviewer's words. Distinct per state rather than one
    /// sentence with a hole in it, because "the factory knows nothing about this
    /// repository" and "the factory read it and there is nothing open" are the two claims
    /// that must never look alike.
    /// </summary>
    public string Says => Status switch
    {
        IntakeStatus.NeverPolled =>
            "not polled yet — this repository has not been read since the factory started",

        IntakeStatus.Polled => OpenIssues is > 0
            ? $"polled at {When(AtUtc)}, and {OpenIssues} open issue(s)"
            : $"polled at {When(AtUtc)}, and nothing is open",

        _ => Failure == FailureClass.Permanent
            ? $"intake failed permanently at {When(AtUtc)}: {Because}. This project is not read again — fix it and "
                + "restart the factory, because project files and the environment are read at start"
            : $"intake failed at {When(AtUtc)}: {Because}. {Failures} failure(s) in a row; this project is read again "
                + $"after {When(AgainAfterUtc)}",
    };

    /// <summary>As the board writes a moment a reviewer reads, in UTC, as the card does.</summary>
    private static string When(DateTimeOffset? at) =>
        at is { } moment ? moment.ToString("yyyy-MM-dd HH:mm 'UTC'") : "an unknown time";
}
