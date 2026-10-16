namespace AgentFactory.Application.Observability;

using AgentFactory.Domain.Rounds;
using System.Diagnostics.Metrics;

/// <summary>
/// The factory's counters. Four instruments on the platform's own metrics API
/// (<see cref="System.Diagnostics.Metrics"/>), and this class is the whole of them: the
/// number is what the design asked for and the names are the ones it is answerable by
/// (DESIGN.md, stories 59 and 63).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this technology, given that the design chose none.</strong> `Meter`,
/// `Counter` and `Histogram` are in the .NET SDK, so this costs the factory **no package
/// reference at all** — which matters here more than it would in a smaller codebase,
/// because the process's whole architecture argument is that it holds three seams and one
/// <c>HttpClient</c>, and every dependency it does not take is a boundary it cannot
/// accidentally grow. The obvious alternative, an OpenTelemetry SDK, is the right answer to a
/// *deployment* question — where the numbers go, who scrapes them — and deployment is #14's,
/// not this ticket's. **The exporter arrived with #63 and it landed on the far side of the
/// boundary**, in `Web`'s `Composition/ServiceDefaults`: what this class needs is still the
/// platform's own API, and nothing in it changed when a pipeline started listening to it.
/// </para>
/// <para>
/// The API is also a <em>push</em> API, and that is still the property that keeps the
/// boundary small: **this class opens nothing.** It creates instruments, and a consumer
/// attaches to the <see cref="Meter"/> in whatever process it shares. A factory that had to
/// expose its own numbers over HTTP would have a second surface with a second set of access
/// rules, reachable by anything that could reach the board. What #63 changed is the
/// *process's* half of that sentence rather than this one: the process now runs an OTLP
/// exporter, which is an <em>outbound</em> connection to a collector on loopback, and an
/// Aspire dashboard — another process — listens. Neither is a surface this class owns, and
/// the decision, and what replaced the retired "publishes no measurement over a socket of any
/// kind", is in `docs/architecture.md`'s Hosting section.
/// </para>
/// <para>
/// <strong>One consequence of that pipeline is worth stating here, because it is a silence
/// this class cannot detect.</strong> An OpenTelemetry <c>MeterProvider</c> collects through
/// its own listener and filters instruments by <em>meter name</em>, so it sees only the meters
/// it was told about — and an instrument on any other meter is dropped with no error at all.
/// So the pipeline must be given <c>AddMeter(<see cref="MeterName"/>)</c>, which it is, in
/// `Composition/ServiceDefaults`, and `Observability/TelemetryPipelineTests` observes a real
/// measurement through it rather than trusting the line. A meter that nothing collects is a
/// remainder that looks exactly like a factory that did nothing.
/// </para>
/// <para>
/// <strong>Why the labels are the ones they are.</strong> A tag on a metric is a dimension
/// of a time series, and every dimension is a set of series that will be kept for ever. A
/// tag whose values are *instances* rather than *classes* — a work item id, a branch, a file
/// path, an issue title — is therefore two failures at once: a time series per work item
/// that no dashboard can read and no backend will keep, and a copy of a stranger's words in
/// a telemetry store whose retention nobody in this repository controls. So the vocabulary
/// is closed and it is <see cref="Tags"/>, which is two keys wide, and
/// <c>PolicyTests</c> asserts by IL scan that this class is the only place in the process
/// that can build a tag at all — so the closed set is a property of the code rather than a
/// convention a later edit is free to ignore.
/// </para>
/// <para>
/// The two keys are a project's name and a round's outcome. A project name is the one
/// instance-valued dimension that survives the argument, and it survives for two reasons:
/// it is bounded by the number of files in <c>factories/</c>, which is a directory a human
/// writes and reviews, and "one noisy repository cannot starve the rest" is a
/// claim about a single repository that is not measurable without it. Everything else the
/// process knows is an instance — a work item, a round, a commit, a path, a reviewer's
/// feedback — and no instrument here is labelled with any of it. What happened to a
/// particular work item is the logs' job, and
/// <see cref="WorkItemScope"/> is how they do it.
/// </para>
/// <para>
/// <strong>Why "builds succeeded" and "builds failed" are one instrument.</strong> Because
/// they are one fact counted twice, and two counters incremented at two call sites can
/// disagree with no way to notice: a path that increments neither is invisible, and a path
/// that increments both is equally invisible. <see cref="Rounds"/> is tagged with the
/// outcome, so <c>outcome=Produced</c> is builds succeeded and <c>outcome=Failed</c> is
/// builds failed, and the identity <c>succeeded + failed == rounds</c> is checkable — which
/// is the property a measurement needs and a pair of counters does not have.
/// </para>
/// </remarks>
public sealed class FactoryMetrics
{
    /// <summary>The meter every instrument below is published on.</summary>
    public const string MeterName = "AgentFactory";

    /// <summary>The tag naming which project's work a measurement is about.</summary>
    public const string ProjectTag = "project";

    /// <summary>The tag naming how a round came back. A closed vocabulary: the round's own enum.</summary>
    public const string OutcomeTag = "outcome";

    /// <summary>
    /// The whole tag vocabulary, and the closed set it is closed <em>by</em>. Exposed so a
    /// test can assert it rather than take it on trust: adding a key is a deliberate edit
    /// here, and this is where a reader looks to see what the cardinality of this process's
    /// metrics is.
    /// </summary>
    public static readonly IReadOnlySet<string> Tags = new HashSet<string>(StringComparer.Ordinal)
    {
        ProjectTag,
        OutcomeTag,
    };

    /// <summary>Issues turned into work items by intake.</summary>
    public const string IssuesProcessedName = "agent_factory.issues.processed";

    /// <summary>Rounds that came back, however they came back.</summary>
    public const string RoundsName = "agent_factory.rounds";

    /// <summary>How many rounds each round spent, as a distribution over the round number.</summary>
    public const string RoundsPerWorkItemName = "agent_factory.rounds_per_work_item";

    private readonly Meter _meter;
    private readonly Counter<long> _issuesProcessed;
    private readonly Counter<long> _rounds;
    private readonly Histogram<long> _roundsPerWorkItem;

    /// <param name="meters">
    /// The host's meter factory, when there is one. A test passes its own so that its
    /// instruments are the only ones published under these names, which is what lets it read
    /// the counters exactly rather than summing over whatever else in the process shares
    /// the meter. Null makes this class own its meter, which is the right answer for a test
    /// and is never the right answer for the process, where the meter has to be the one the
    /// host can hand a consumer.
    /// </param>
    public FactoryMetrics(IMeterFactory? meters = null) : this((meters ?? Own()).Create(MeterName))
    {
    }

    public FactoryMetrics(Meter meter)
    {
        _meter = meter ?? throw new ArgumentNullException(nameof(meter));
        _issuesProcessed = meter.CreateCounter<long>(IssuesProcessedName);
        _rounds = meter.CreateCounter<long>(RoundsName);
        _roundsPerWorkItem = meter.CreateHistogram<long>(RoundsPerWorkItemName);
    }

    /// <summary>
    /// The meter these instruments live on, so that a consumer listening for them can be
    /// sure it is listening to this instance's and not another's with the same name.
    /// </summary>
    public Meter Meter => _meter;

    /// <summary>
    /// A meter factory of this class's own, for the case where there is no host. It is one
    /// instance of one class, and it is what makes this component constructible in a test
    /// with no service provider in it — which is the only reason it is here rather than a
    /// required dependency.
    /// </summary>
    private static IMeterFactory Own() => new StandaloneMeterFactory();

    private sealed class StandaloneMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options.Name, options.Version, options.Tags, options.Scope);
            lock (_meters)
            {
                _meters.Add(meter);
            }

            return meter;
        }

        public void Dispose()
        {
            lock (_meters)
            {
                foreach (var meter in _meters)
                {
                    meter.Dispose();
                }

                _meters.Clear();
            }
        }
    }

    /// <summary>
    /// One issue became a work item. Counted when intake <em>created</em> the work item and
    /// not when it re-read the issue, because intake is idempotent against the store
    /// and a counter that went up on every pass would measure how often the
    /// poller ticks rather than how much work arrived.
    /// </summary>
    public void IssueProcessed(string project) => _issuesProcessed.Add(1, Project(project));

    /// <summary>
    /// One round came back and the factory recorded it. The outcome is the tag rather than
    /// the instrument name, for the reason this class's own remarks give: the two are one
    /// count, and one count can be checked for consistency with the other.
    /// </summary>
    public void RoundCameBack(string project, RoundOutcome outcome) => _rounds.Add(1, [
        Key(ProjectTag, project),
        Key(OutcomeTag, outcome.ToString()),
    ]);

    /// <summary>
    /// How many rounds a work item had spent when this one landed. Recorded once per round
    /// landed, with the round's own number as the value — so the histogram is the cost of
    /// rounds in aggregate and its buckets differ to give how many work items reached each
    /// round. It is deliberately not a label: "rounds per work item" as a tagged counter
    /// would be one series per work item, which is the cardinality and privacy hazard this
    /// class exists to refuse.
    /// </summary>
    public void RoundsSpentOnThisWorkItem(string project, int rounds) =>
        _roundsPerWorkItem.Record(rounds, Project(project));

    /// <summary>
    /// The one tag: which project. A project name, never a work item, a branch, a path, a
    /// URL, an issue title or anything else an instance is made of.
    /// </summary>
    private static KeyValuePair<string, object?> Project(string project) => Key(ProjectTag, project);

    /// <summary>
    /// One tag. Every tag in this process is built here, which is what makes
    /// <see cref="Tags"/> a closed vocabulary by shape rather than by good intentions.
    /// </summary>
    private static KeyValuePair<string, object?> Key(string tag, object? value)
    {
        if (!Tags.Contains(tag))
        {
            // A throw rather than a warning: this class is the only place a tag can be
            // built, so a key that is not in the closed set can only be a mistake here, and
            // a metric published with an unrecognised dimension is a time series nobody can
            // reason about once it exists.
            throw new ArgumentOutOfRangeException(
                nameof(tag), tag, "the factory's metric tags are a closed vocabulary of classes, not of instances");
        }

        return new KeyValuePair<string, object?>(tag, value);
    }
}
