namespace AgentFactory.Tests.Observability;

using OpenTelemetry;
using OpenTelemetry.Metrics;

/// <summary>
/// What the process's OpenTelemetry pipeline actually handed an exporter, read the way an
/// exporter reads it (#63).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists next to <see cref="RecordedMeasurements"/>, which reads the same
/// instruments.</strong> That one is a <see cref="System.Diagnostics.Metrics.MeterListener"/>
/// attached to the <see cref="AgentFactory.Application.Observability.FactoryMetrics"/>
/// meter, and it is the right reader for every claim about what the factory counts. It is
/// deliberately *not* the right reader for the claim this class is for, because a
/// <c>MeterListener</c> sees an instrument whether or not any OpenTelemetry provider was told
/// about its meter — it bypasses the pipeline entirely. The claim under test is that the
/// pipeline was told, so the reader has to be *inside* it: this is a
/// <see cref="BaseExportingMetricReader"/> contributed to the same
/// <see cref="MeterProvider"/> the factory builds, so a measurement appears here only if it
/// survived the provider's own meter filter.
/// </para>
/// <para>
/// It is not a fake and it exports nothing: the reader's collector is in-process, there is no
/// network, and the values it holds are what an OTLP exporter would have been handed.
/// </para>
/// </remarks>
public sealed class ExportedMetrics : IDisposable
{
    private readonly Collector _collector = new();
    private readonly BaseExportingMetricReader _reader;

    public ExportedMetrics() => _reader = new BaseExportingMetricReader(_collector);

    /// <summary>The reader itself, to be contributed to the provider under test.</summary>
    public MetricReader Reader => _reader;

    /// <summary>Every instrument name that reached the exporter, distinct.</summary>
    public IReadOnlyList<string> Names => [.. _collector.Points.Select(point => point.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>Every measurement that reached the exporter.</summary>
    public IReadOnlyList<Point> Points => _collector.Points;

    /// <summary>Every measurement of one instrument. Empty is a real answer here, and the point.</summary>
    public IReadOnlyList<Point> Of(string instrument) =>
        [.. _collector.Points.Where(point => string.Equals(point.Name, instrument, StringComparison.Ordinal))];

    /// <summary>Every value a measurement of one instrument carried.</summary>
    public IReadOnlyList<long> Values(string instrument) => [.. Of(instrument).Select(point => point.Value)];

    public void Dispose() => _reader.Dispose();

    /// <summary>One measurement an exporter was handed: what it was, how much, and labelled with what.</summary>
    public sealed record Point(string Name, MetricType Type, long Value, IReadOnlyList<KeyValuePair<string, object?>> Tags)
    {
        /// <summary>The value a tag carries, or null when this measurement has no such tag.</summary>
        public object? Tag(string key) => Tags
            .FirstOrDefault(tag => string.Equals(tag.Key, key, StringComparison.Ordinal)).Value;
    }

    /// <summary>
    /// The exporter, in process: it keeps every point it was handed and returns success. A
    /// failure result would make the SDK report a dropped batch, which is a fact about the
    /// collector rather than about the pipeline being observed.
    /// </summary>
    private sealed class Collector : BaseExporter<Metric>
    {
        private readonly List<Point> _points = [];

        public IReadOnlyList<Point> Points
        {
            get
            {
                lock (_points)
                {
                    return [.. _points];
                }
            }
        }

        public override ExportResult Export(in Batch<Metric> batch)
        {
            lock (_points)
            {
                foreach (var metric in batch)
                {
                    foreach (var point in metric.GetMetricPoints())
                    {
                        _points.Add(new Point(metric.Name, metric.MetricType, Value(point, metric.MetricType), [.. point.Tags]));
                    }
                }
            }

            return ExportResult.Success;
        }

        /// <summary>
        /// How much a point measured, as a whole number. Every instrument this factory defines
        /// is a <c>Counter&lt;long&gt;</c>, which is the shape this reads: a long sum, exactly.
        /// A distribution is read as its count and anything else as zero, because throwing
        /// inside an exporter would turn "the pipeline carried a metric this test does not read"
        /// into a failure of the pipeline.
        /// </summary>
        private static long Value(MetricPoint point, MetricType type) => type switch
        {
            MetricType.LongSum or MetricType.LongSumNonMonotonic => point.GetSumLong(),
            MetricType.Histogram or MetricType.ExponentialHistogram => point.GetHistogramCount(),
            _ => 0,
        };
    }
}
