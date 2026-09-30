namespace AgentFactory;

/// <summary>
/// Factory behaviour is code, never configuration. Every project gets identical
/// behaviour, so these live here and nowhere else.
/// </summary>
public static class FactoryConstants
{
    /// <summary>The work item gets at most three rounds.</summary>
    public const int RoundCeiling = 3;

    /// <summary>
    /// How long a work item may sit in Review before the factory merges it rather than
    /// hold the pipeline behind a reviewer who is not watching. A code constant and not
    /// configuration, like everything else here: every project gets the same threshold
    /// and changing it is a deliberate edit in one obvious place.
    /// </summary>
    /// <remarks>
    /// Measured against <c>IClock</c> from the moment the work item entered Review, so a
    /// test drives it by advancing time and nothing waits — and so the threshold is a
    /// comparison the loop makes rather than a timer it starts. It is measured in
    /// wall-clock time and is not interchangeable with the round ceiling: a reviewer who
    /// sends changes back quickly spends rounds, and a reviewer who walks away spends
    /// this (ADR-0001).
    /// </remarks>
    public static readonly TimeSpan FeedbackThreshold = TimeSpan.FromHours(48);

    /// <summary>
    /// The threshold as a reviewer reads it, in one place, so the board, the log and the
    /// loop's refusals cannot end up saying three different things about how long
    /// silence takes to ship a change.
    /// </summary>
    public static string FeedbackThresholdText => $"{FeedbackThreshold.TotalHours:0} hours";

    /// <summary>
    /// A round is bounded at ninety minutes, so a session that never comes back cannot
    /// hold a slot for ever. Measured against the clock, not a timer, so a test drives
    /// it by advancing time rather than by waiting.
    /// </summary>
    public static readonly TimeSpan RoundTimeout = TimeSpan.FromMinutes(90);

    /// <summary>
    /// How many times a round is asked for before a transient failure is given up on, and
    /// a failed merge is parked. Three attempts in total: the first, and two more. A code
    /// constant and not configuration, like everything else here, so every project gets
    /// the same ceiling and changing it is a deliberate edit in one obvious place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This counts <em>attempts</em> and not rounds. A container that would not start is
    /// not a round that ran and failed: it is a round that did not happen, and charging it
    /// against the round ceiling would spend a reviewer's budget on the factory's own
    /// infrastructure (ADR-0001).
    /// </para>
    /// <para>
    /// It is the same number for a round and for a merge, deliberately. Both are attempts
    /// at one operation, and the reason to stop after three is the same in both cases: a
    /// fourth tells nobody anything a third did not, and for a merge the fourth is
    /// something the repository has to be asked to take.
    /// </para>
    /// </remarks>
    public const int TransientRetryAttempts = 3;

    /// <summary>
    /// The first wait between attempts. Each one after it is double the one before, so the
    /// waits are ten seconds, twenty, and then no more — there is no fourth attempt to wait
    /// for.
    /// </summary>
    /// <remarks>
    /// A <see cref="TimeSpan"/> and not a sleep. Nothing in the factory waits on one: a
    /// backoff is a comparison against <c>IClock</c>, and a step asked before the wait has
    /// passed is refused and does nothing. That is what lets a test drive three attempts
    /// with backoff in microseconds, and it is why the loop has no timer in it.
    /// </remarks>
    public static readonly TimeSpan RetryBackoffBase = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long to wait after an attempt failed, given how many attempts have been made.
    /// Exponential in the attempt number, and capped at the last attempt there is: asking
    /// for a wait past the end of the ceiling would be a wait for something that is never
    /// going to be asked for.
    /// </summary>
    /// <param name="attempt">
    /// The attempt that has just failed, counted from one. Anything beyond the last one
    /// gets the last one's wait rather than an unbounded one.
    /// </param>
    public static TimeSpan RetryBackoff(int attempt)
    {
        var last = Math.Clamp(attempt, 1, TransientRetryAttempts) - 1;
        return TimeSpan.FromTicks(RetryBackoffBase.Ticks * (1L << last));
    }

    /// <summary>How often the board refreshes itself.</summary>
    public const int BoardAutoRefreshSeconds = 5;

    /// <summary>
    /// How many worker containers the factory may have running at once. Two, sized for
    /// this machine's 7.19 GB, in which each worker runs an agent, a toolchain and a
    /// build: three would have them fighting each other for memory rather than building.
    /// A code constant and not configuration, like everything else here, so every project
    /// gets the same budget and changing it is a deliberate edit in one obvious place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The budget is a count of <em>containers</em>, and it is the only thing that bounds
    /// concurrency. It is deliberately not a lock, not a shared working tree and not a
    /// per-project quota: each round gets its own fresh container whatever project it is
    /// for (ADR-0001), so there is nothing to serialise and nothing to share, and a
    /// project's own long build cannot hold another project's issue out of a container.
    /// </para>
    /// <para>
    /// It is not the same number as <see cref="TransientRetryAttempts"/>, and the two
    /// must not be conflated: that one counts how many times one round is asked for, and
    /// this one counts how many rounds exist at a time. A round waiting out its retry
    /// backoff still holds a container slot — it has not ended — so three waiting retries
    /// cannot happen at all, because a budget of two never admits a third round to wait.
    /// </para>
    /// </remarks>
    public const int ContainerBudget = 2;

    /// <summary>
    /// How often the driver steps the machine: intake takes a turn and the loop applies
    /// whatever it can. It is a tick rather than a wait — the driver asks, and nothing
    /// inside the policy is holding anything up — and it is short enough that a finished
    /// round is noticed while the board is still refreshing itself.
    /// </summary>
    /// <remarks>
    /// The one place in the process with a <c>Task.Delay</c> in it, and the one legitimate
    /// exception to there being no waits anywhere: everything the policy defers is a
    /// <see cref="TimeSpan"/> compared against <c>IClock</c>, and only the driver needs to
    /// know that time passed on its own. <c>PolicyTests</c> says so by naming this one
    /// call site rather than by allowing waits in general.
    /// </remarks>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest intake will wait before asking a project again, however long it has
    /// been failing. Above this the growth stops, and the trade is stated rather than
    /// assumed: a project that has been down for a day is read a handful of times instead
    /// of fourteen hundred, and a project that comes back is picked up within sixteen
    /// minutes rather than waiting out an exponential that has grown past a week.
    /// </summary>
    /// <remarks>
    /// This is intake's own schedule and not the round's, because the two are answering
    /// different questions. A round is an attempt at building something, and there are only
    /// ever three of them; a poll is a read, and there is nothing to escalate when it
    /// fails, so the answer to "how often should we try again" is "less and less often",
    /// not "three times and then never".
    /// </remarks>
    public static readonly TimeSpan PollBackoffCeiling = TimeSpan.FromMinutes(16);

    /// <summary>
    /// How long before a project that has failed this many times in a row is read again.
    /// Doubles per consecutive failure like <see cref="RetryBackoff"/>, but is not capped
    /// at the attempt count — there is no last attempt here — and is capped in time
    /// instead, by <see cref="PollBackoffCeiling"/>.
    /// </summary>
    /// <param name="failures">How many times in a row this project has failed.</param>
    public static TimeSpan PollBackoff(int failures)
    {
        // Doubled by halving the room left rather than by shifting, so the arithmetic
        // cannot overflow on a project that has been failing since the process started: a
        // failure count in the thousands is a schedule, not a number to exponentiate.
        var backoff = RetryBackoffBase;
        for (var doubling = 1; doubling < failures && backoff < PollBackoffCeiling; doubling++)
        {
            backoff += backoff;
        }

        return backoff < PollBackoffCeiling ? backoff : PollBackoffCeiling;
    }

    /// <summary>
    /// How often a project pass runs over the projects the factory serves. Measured
    /// against the clock rather than waited on, so a test drives it by advancing time
    /// instead of sleeping, and so a slow pass cannot become a fast one.
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The host ports a review workspace may be published on, loopback only. A code
    /// constant and not configuration, like everything else here — the exposure decision
    /// (#30) put the range in the factory rather than in a compose file, so a workspace is
    /// never reachable past the machine that decided to open it. Exhaustion is a rendered
    /// state on the card, never silence.
    /// </summary>
    public const int WorkspacePortFloor = 7100;

    /// <summary>The last host port of the workspace range, inclusive.</summary>
    public const int WorkspacePortCeiling = 7199;

    /// <summary>
    /// The port code-server listens on inside a review workspace. Nothing outside the
    /// container's own publish decides it, so the whole mapping is one fact the factory
    /// states twice at most: here and in the create it composes.
    /// </summary>
    public const int CodeServerPort = 6800;

    /// <summary>
    /// How long a review workspace lives from spawn. A fixed lifetime rather than an idle
    /// detection, because nothing inside a workspace can report idleness without new
    /// machinery: it ends on the reviewer's decision, on a new round starting, or here —
    /// and re-opening one is a click that always reflects the current tree (#31).
    /// </summary>
    public static readonly TimeSpan WorkspaceLifetime = TimeSpan.FromHours(4);
}
