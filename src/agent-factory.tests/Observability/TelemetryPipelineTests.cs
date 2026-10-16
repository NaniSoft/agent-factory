namespace AgentFactory.Tests.Observability;

using AgentFactory.Application.GitHub;
using AgentFactory.Application.Observability;
using AgentFactory.Tests.Boundary;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using System.Diagnostics.Metrics;

/// <summary>
/// The process's telemetry pipeline, observed rather than asserted about (#63).
/// </summary>
/// <remarks>
/// <para>
/// The ticket's second finding is a silence. An OpenTelemetry <c>MeterProvider</c> does not
/// implement <see cref="IMeterFactory"/> — the SDK collects through its own
/// <c>MeterListener</c> and filters instruments by <em>meter name</em> — so
/// <c>FactoryMetrics</c>' constructor is untouched, and an instrument on a meter the provider
/// was never told about is dropped with no error, no warning and no failing test. Adding
/// <c>AddMeter(FactoryMetrics.MeterName)</c> and calling it verified because it looks right is
/// exactly the kind of "call you added and did not verify" this suite refuses elsewhere, so
/// the line is observed here end to end: the production composition root builds the provider,
/// a real factory publishes a real measurement, and what is asserted is what an exporter was
/// handed.
/// </para>
/// <para>
/// The reader is <see cref="ExportedMetrics"/> — a <see cref="BaseExportingMetricReader"/>
/// inside the pipeline — rather than the suite's
/// <see cref="RecordedMeasurements"/> <c>MeterListener</c>. That distinction is the whole
/// reason this file can make the claim: a listener sees the meter whether or not any provider
/// subscribed to it, so it could not tell the two cases apart. The control below is what says
/// so out loud.
/// </para>
/// </remarks>
public class TelemetryPipelineTests
{
    /// <summary>
    /// A project name no other test uses, and that is load-bearing rather than tidy. An
    /// OpenTelemetry provider subscribes to a meter by *name*, so while this suite runs its
    /// hosts in parallel every live provider whose pipeline names `AgentFactory` sees the
    /// instruments of every host that has one — one `Metric` per instrument name, with a point
    /// per labelled set. A filter on a tag value two tests share would therefore be reading
    /// other factories' numbers, which is a test that passes or fails on how many neighbours it
    /// had. This one's tag is its own. (The name is a single word because
    /// <see cref="ProjectFile.For"/> derives the credential names from it and a project file's
    /// key names have to be environment-variable names.)
    /// </summary>
    private const string Probe = "telemetryprobe";

    private const string ProbeRepo = "https://github.com/NaniSoft/telemetryprobe";

    [Fact]
    public async Task The_factorys_own_instrument_reaches_an_exporter_because_its_meter_is_subscribed()
    {
        // The factory's own counter, published by the real poller, through the real pipeline.
        // If `AddMeter` were removed from `Composition/ServiceDefaults` this is the test that
        // would fail — the instrument would still exist, the count would still be published,
        // and the provider would drop it without a word.
        using var root = FactoryRoot.Create().WithProjectFile($"{Probe}.yaml", ProjectFile.For(Probe, ProbeRepo));
        using var exported = new ExportedMetrics();
        await using var host = await FactoryHost.ObservingTelemetryAsync(root, exported.Reader);

        host.GitHub.WithRepository(
            ProbeRepo,
            "main",
            new OpenIssue(1, "one", "build it", [], []),
            new OpenIssue(2, "two", "build this", [], []));

        await host.PollAsync();

        // Flush the provider the factory built — resolved from its own container rather than
        // constructed here — so what is read is the process's pipeline and not a copy of it.
        Assert.True(
            host.Services.GetRequiredService<MeterProvider>().ForceFlush(30_000),
            "the process's meter provider did not flush within thirty seconds");

        var counted = exported.Of(FactoryMetrics.IssuesProcessedName)
            .Where(point => Equals(point.Tag(FactoryMetrics.ProjectTag), Probe))
            .ToList();

        // The instrument reached the exporter, once, with the two work items intake made on it
        // and the one tag the closed vocabulary allows.
        var point = Assert.Single(counted);
        Assert.Equal(2, point.Value);
        Assert.Equal(Probe, point.Tag(FactoryMetrics.ProjectTag));

        // And the process's own runtime is on the same pipeline, which is the other half of
        // "telemetry always on": the factory's instruments are not the only thing the AppHost's
        // dashboard has to show. A reader that found the factory's meter and nothing else would
        // mean the instrumentation was registered for one signal source only, and these are the
        // runtime instrumentation's own names (`dotnet.gc.*`, `dotnet.thread_pool.*`).
        Assert.Contains(exported.Names, name => name.StartsWith("dotnet.", StringComparison.Ordinal));
    }

    [Fact]
    public void An_instrument_on_a_meter_the_pipeline_was_not_told_about_is_dropped_without_a_word()
    {
        // **The control, and the reason the test above means something.** It establishes the
        // negative that the ticket is about: a provider with a reader and no `AddMeter` collects
        // nothing from a meter it was not told about, silently. Without this, the test above
        // would pass just as well against a pipeline that collected every meter in the process —
        // and "AddMeter is load-bearing" would be an untested claim.
        //
        // It is deliberately built here rather than by removing the line from production code,
        // because the thing under test is not this test's own configuration: this is the *SDK's*
        // behaviour, reproduced, and the reader's ability to see an absence is what is being
        // shown. The provider the factory builds is the one the test above reads.
        using var exported = new ExportedMetrics();
        using var provider = Sdk.CreateMeterProviderBuilder()
            .AddReader(exported.Reader)
            .Build();

        using var meter = new Meter(FactoryMetrics.MeterName);
        meter.CreateCounter<long>(FactoryMetrics.IssuesProcessedName)
            .Add(1, new KeyValuePair<string, object?>(FactoryMetrics.ProjectTag, Probe));

        provider.ForceFlush(30_000);

        Assert.Empty(exported.Names);
        Assert.Empty(exported.Points);
    }
}
