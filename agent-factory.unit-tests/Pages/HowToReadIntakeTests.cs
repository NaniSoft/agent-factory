namespace AgentFactory.UnitTests.Pages;

using AgentFactory.Application.Pages;
using AgentFactory.Application.Polling;
using AgentFactory.Domain.Failures;
using AgentFactory.UnitTests.Boundary;

/// <summary>
/// What the board says about intake. An empty Backlog lane used to be one rendered thing standing
/// for three facts — the repository was read and had nothing open, the factory has never read it, or
/// the factory tried and was refused — and this is the judgement that tells them apart.
/// </summary>
public class HowToReadIntakeTests
{
    [Fact]
    public void The_three_states_have_three_slugs_and_a_fourth_is_refused()
    {
        Assert.Equal("never-polled", HowToReadIntake.Slug(IntakeStatus.NeverPolled));
        Assert.Equal("polled", HowToReadIntake.Slug(IntakeStatus.Polled));
        Assert.Equal("failing", HowToReadIntake.Slug(IntakeStatus.Failing));

        // A state nobody has argued for is thrown rather than rendered as something: a state the
        // board cannot name is a state nobody can be told about.
        Assert.Throws<ArgumentOutOfRangeException>(() => HowToReadIntake.Slug((IntakeStatus)99));
    }

    [Fact]
    public void Each_keeps_the_rotation_order_and_carries_an_open_count_only_where_there_was_a_read()
    {
        var rows = HowToReadIntake.Each(
        [
            Record("alpha", IntakeStatus.Polled, openIssues: 3),
            Record("beta", IntakeStatus.NeverPolled),
            Record("gamma", IntakeStatus.Failing, failure: FailureClass.Permanent, because: "not found"),
        ]);

        Assert.Equal(["alpha", "beta", "gamma"], rows.Select(row => row.Project));

        // Null rather than zero for the two states where there has not been a read: zero is a real
        // answer and rendering it beside a read that did not happen would be inventing one.
        Assert.Equal(3, rows[0].OpenIssues);
        Assert.Null(rows[1].OpenIssues);
        Assert.Null(rows[2].OpenIssues);
    }

    [Fact]
    public void The_three_states_read_as_three_different_things()
    {
        var rows = HowToReadIntake.Each(
        [
            Record("alpha", IntakeStatus.NeverPolled),
            Record("beta", IntakeStatus.Polled, openIssues: 0),
            Record("gamma", IntakeStatus.Failing, failure: FailureClass.Permanent, because: "not found"),
        ]);

        Assert.Contains("not polled yet", rows[0].Says, StringComparison.Ordinal);
        Assert.Contains("nothing is open", rows[1].Says, StringComparison.Ordinal);
        Assert.Contains("failed permanently", rows[2].Says, StringComparison.Ordinal);

        Assert.Equal(rows.Count, rows.Select(row => row.Says).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_permanent_failure_is_not_read_again_and_the_row_says_so()
    {
        var rows = HowToReadIntake.Each(
        [
            Record("alpha", IntakeStatus.Failing, failure: FailureClass.Permanent, because: "no such repository"),
        ]);

        Assert.Equal("never", rows[0].Again);
        Assert.Contains("not read again", rows[0].Says, StringComparison.Ordinal);
        Assert.Contains("restart the factory", rows[0].Says, StringComparison.Ordinal);
    }

    [Fact]
    public void A_transient_failure_says_when_the_project_will_be_read_again()
    {
        var again = AWorkItem.At.AddMinutes(10);
        var rows = HowToReadIntake.Each(
        [
            Record("alpha", IntakeStatus.Failing, failure: FailureClass.Transient, because: "503", againAfter: again, failures: 2),
        ]);

        Assert.Equal(again.ToString("O"), rows[0].Again);
        Assert.Contains("2 failure(s) in a row", rows[0].Says, StringComparison.Ordinal);
        Assert.Contains("read again after", rows[0].Says, StringComparison.Ordinal);
    }

    [Fact]
    public void A_project_that_is_fine_is_read_on_the_next_pass()
    {
        var rows = HowToReadIntake.Each([Record("alpha", IntakeStatus.Polled, openIssues: 1)]);

        Assert.Equal("next-pass", rows[0].Again);
        Assert.Contains("1 open issue(s)", rows[0].Says, StringComparison.Ordinal);
    }

    [Fact]
    public void The_worst_of_the_projects_is_the_one_a_reader_needs_first()
    {
        // A failure beats both because the factory has an answer and the answer is no; never-polled
        // beats polled because the factory has established nothing at all about the repository.
        Assert.Equal(IntakeStatus.Polled, WorstOf(Record("a", IntakeStatus.Polled)));
        Assert.Equal(IntakeStatus.NeverPolled, WorstOf(Record("a", IntakeStatus.Polled), Record("b", IntakeStatus.NeverPolled)));
        Assert.Equal(IntakeStatus.Failing, WorstOf(Record("a", IntakeStatus.NeverPolled), Record("b", IntakeStatus.Failing)));
        Assert.Equal(IntakeStatus.Failing, WorstOf(Record("a", IntakeStatus.Polled), Record("b", IntakeStatus.Failing)));
    }

    [Fact]
    public void The_summary_says_an_empty_lane_does_not_mean_there_is_nothing_to_do_whenever_it_does_not()
    {
        // This sentence is the fix for #16 rather than the rows below it: a fault named in a list a
        // reviewer has to know to look at is a fault that reads as an empty lane.
        var healthy = HowToReadIntake.Each([Record("alpha", IntakeStatus.Polled, openIssues: 0)]);
        var never = HowToReadIntake.Each([Record("alpha", IntakeStatus.NeverPolled)]);
        var failing = HowToReadIntake.Each([Record("alpha", IntakeStatus.Failing, failure: FailureClass.Permanent, because: "gone")]);

        Assert.Equal("Intake is reading every project the factory serves.", HowToReadIntake.Summarise(healthy));
        Assert.DoesNotContain("An empty Backlog", HowToReadIntake.Summarise(healthy), StringComparison.Ordinal);

        Assert.Contains("1 of 1 project(s) have not been polled yet", HowToReadIntake.Summarise(never), StringComparison.Ordinal);
        Assert.Contains("An empty Backlog does not mean there is nothing to do", HowToReadIntake.Summarise(never), StringComparison.Ordinal);

        Assert.Contains("1 of 1 project(s) cannot be read", HowToReadIntake.Summarise(failing), StringComparison.Ordinal);
        Assert.Contains("An empty Backlog does not mean there is nothing to do", HowToReadIntake.Summarise(failing), StringComparison.Ordinal);
    }

    [Fact]
    public void A_factory_serving_nothing_says_so_rather_than_reading_as_an_empty_lane()
    {
        Assert.Equal(
            "The factory is serving no projects, so there is nothing to poll.",
            HowToReadIntake.Summarise([]));
    }

    private static IntakeStatus WorstOf(params IntakeRecord[] records) =>
        HowToReadIntake.WorstOf(HowToReadIntake.Each(records));

    private static IntakeRecord Record(
        string project,
        IntakeStatus status,
        int openIssues = 0,
        FailureClass? failure = null,
        string? because = null,
        DateTimeOffset? againAfter = null,
        int failures = 0) =>
        new(
            project,
            $"https://github.com/NaniSoft/{project}",
            status,
            openIssues,
            failure,
            because,
            AWorkItem.At,
            failures,
            againAfter);
}
