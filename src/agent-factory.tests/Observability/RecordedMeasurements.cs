namespace AgentFactory.Tests.Observability;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AgentFactory.Observability;

/// <summary>
/// Every measurement the factory published, read the way any consumer would read it: an
/// in-process <see cref="MeterListener"/> attached to the meter, with no network, no port
/// and no exporter — which is the whole point of the technology the factory chose.
/// </summary>
/// <remarks>
/// <para>
/// It listens to one specific <see cref="Meter"/> rather than to the instrument names,
/// because the names are what the assertions are about and a listener matching on them
/// would be counting another test's factory. <see cref="FactoryMetrics"/> hands out the
/// meter it published on for exactly this reason.
/// </para>
/// <para>
/// The tags are kept on every measurement and never filtered, because the cardinality claim
/// is only testable if the test can see everything that was published: a filter here that
/// quietly dropped a tag would make the very failure it exists to catch invisible.
/// </para>
/// </remarks>
public sealed class RecordedMeasurements : IDisposable
{
    private readonly MeterListener _listener;
    private readonly ConcurrentQueue<Measurement> _measurements = new();
    private readonly ConcurrentDictionary<string, byte> _published = new(StringComparer.Ordinal);

    public RecordedMeasurements(FactoryMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (!ReferenceEquals(instrument.Meter, metrics.Meter))
                {
                    return;
                }

                _published.TryAdd(instrument.Name, 0);
                listener.EnableMeasurementEvents(instrument);
            },
        };

        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            _measurements.Enqueue(new Measurement(instrument.Name, measurement, [.. tags])));

        _listener.Start();
    }

    /// <summary>Every measurement published, in the order it was published.</summary>
    public IReadOnlyList<Measurement> All => [.. _measurements];

    /// <summary>Every instrument the factory published at all, whether or not anything recorded on it.</summary>
    public IReadOnlyList<string> Instruments => [.. _published.Keys.Order(StringComparer.Ordinal)];

    /// <summary>
    /// Every measurement of one instrument, and a failure rather than a silent empty list if
    /// the instrument was never published. A test that asked for a counter nobody defined
    /// would otherwise be told the same thing as a test that asked for a counter nobody
    /// incremented, and the difference between "this does not exist" and "this has not
    /// happened yet" is the difference between a broken name and a working measurement.
    /// </summary>
    public IReadOnlyList<Measurement> Of(string instrument)
    {
        if (!_published.ContainsKey(instrument))
        {
            throw new InvalidOperationException(
                $"no instrument named {instrument} was published on the factory's meter; it published: "
                    + string.Join(", ", Instruments));
        }

        return [.. _measurements.Where(measurement => measurement.Instrument == instrument)];
    }

    /// <summary>How many measurements of one instrument there were, optionally about one tag value.</summary>
    public long Count(string instrument, string? outcome = null) =>
        Of(instrument).Count(measurement => outcome is null
            || Equals(measurement.Tag(FactoryMetrics.OutcomeTag), outcome));

    /// <summary>How many measurements of one instrument carried a tag with one value.</summary>
    public long Count(string instrument, string tag, string tagValue) =>
        Of(instrument).Count(measurement => Equals(measurement.Tag(tag), tagValue));

    /// <summary>Every value an instrument recorded, for measurements carrying one tag value.</summary>
    public IReadOnlyList<long> Values(string instrument, string tag, string tagValue) =>
        [.. Of(instrument)
            .Where(measurement => Equals(measurement.Tag(tag), tagValue))
            .Select(measurement => measurement.Value)];

    /// <summary>Every tag key any published measurement carried, across every instrument.</summary>
    public IReadOnlyList<string> TagKeys =>
        [.. _measurements.SelectMany(measurement => measurement.Tags).Select(tag => tag.Key)
            .Distinct().Order(StringComparer.Ordinal)];

    /// <summary>Every tag value any published measurement carried, as text.</summary>
    public IReadOnlyList<string> TagValues =>
        [.. _measurements.SelectMany(measurement => measurement.Tags)
            .Select(tag => tag.Value?.ToString() ?? string.Empty)
            .Distinct().Order(StringComparer.Ordinal)];

    /// <summary>
    /// Everything an exporter would have written, as one blob: a metric is the instrument's
    /// name, its tags and its value, and this is all three for every one of them. It is what
    /// a "no credential appears in any measurement" check reads.
    /// </summary>
    public string Everything => string.Join('\n', _measurements.Select(measurement => measurement.Everything));

    public void Dispose() => _listener.Dispose();

    /// <summary>One published measurement: what it was, how much, and labelled with what.</summary>
    public sealed record Measurement(
        string Instrument,
        long Value,
        IReadOnlyList<KeyValuePair<string, object?>> Tags)
    {
        /// <summary>The value a tag carries, or null when this measurement has no such tag.</summary>
        public object? Tag(string key) => Tags
            .FirstOrDefault(tag => string.Equals(tag.Key, key, StringComparison.Ordinal)).Value;

        /// <summary>The measurement as an exporter would write it.</summary>
        public string Everything =>
            $"{Instrument}{{{string.Join(',', Tags.Select(tag => $"{tag.Key}={tag.Value}"))}}}={Value}";
    }
}
