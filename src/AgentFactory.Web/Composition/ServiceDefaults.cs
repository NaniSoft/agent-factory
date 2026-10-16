namespace AgentFactory.Composition;

using AgentFactory.Application.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

/// <summary>
/// The factory's service defaults: the telemetry pipeline, and the OTLP exporter it is handed
/// a destination by (#63). Everything the process does that is neither a seam nor policy is
/// here.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is not Aspire's <c>AddServiceDefaults</c>, and the difference is the ticket.</strong>
/// The template's version is four calls:
/// <c>ConfigureOpenTelemetry()</c>, <c>AddDefaultHealthChecks()</c>,
/// <c>AddServiceDiscovery()</c>, and <c>ConfigureHttpClientDefaults(AddStandardResilienceHandler)</c>.
/// One of the four is kept; the other three are absent, and each absence has its own argument
/// below:
/// </para>
/// <list type="number">
/// <item>
/// <term>The standard resilience handler is not installed, and its package is not referenced.</term>
/// <description>
/// <c>ConfigureHttpClientDefaults</c> applies to *every* named and typed <c>HttpClient</c>,
/// and the GitHub client is a named one — so the template's defaults would put a Polly
/// pipeline on the factory's one transport. Three things follow from that, and each is a
/// failure this codebase has a pin against: its <em>attempt</em> timeout is 10 seconds where
/// <c>GitHubClient.RequestTimeout</c> is the bound the factory chose (30), and it signals by
/// throwing <c>TimeoutRejectedException</c>, which is not a <c>TimeoutException</c> and is not
/// an <c>HttpRequestException</c> — so `GitHubClient.AskAsync` catches neither, the exception
/// escapes the seam, and it is classified by <c>Failures.Classify</c> from a Polly message
/// instead of by the component that observed it, which is the one thing the whole
/// <c>GitHubResponse</c>/<c>Failures</c> design exists to prevent. It <em>retries</em>, by
/// default, for every HTTP method — including the merge, the pull-request create and the issue
/// close, so the merger's own idempotency story gains an invisible layer. And those retries
/// are a *second* retry policy stacked on the deliberate one in `Orchestrator.Approve`, which
/// is bounded at three attempts, classified, and counted on the work item.
/// <para>
/// The prevention is structural in two places and neither depends on anybody remembering it.
/// The package is not a `PackageReference` of any project —
/// <c>ArchitectureTests.No_project_takes_the_http_resilience_package</c>, so
/// <c>AddStandardResilienceHandler</c> is not a name that compiles — and
/// <c>Boundary/TheProcessTests.The_github_clients_pipeline_carries_no_second_retry_policy</c>
/// walks the handler chain the running factory really builds for <c>github</c> and asserts that
/// nothing in it comes from the resilience package or from Polly, which catches the handler
/// arriving by any route at all. Both pins were proved to fire rather than assumed to: the
/// handler was installed, and both went red.
/// </para>
/// </description>
/// </item>
/// <item>
/// <term>Service discovery is not registered, because nothing here resolves a service name.</term>
/// <description>
/// GitHub is an absolute URL a project file states and the AppHost passes Temporal's address as
/// <c>host:port</c> in <c>Factory__Temporal__Endpoint</c>. A discovery layer would resolve
/// nothing while being a dependency, a hosted service and a configuration surface nobody asked
/// for.
/// </description>
/// </item>
/// <item>
/// <term>The health-check endpoints are not mapped, and that is a decision rather than an omission.</term>
/// <description>
/// <c>app.MapDefaultEndpoints()</c> maps <c>/health</c> and <c>/alive</c> — <em>only</em> in the
/// Development environment, which is exactly the environment the test host and every
/// <c>dotnet run</c> use. That is two new anonymous, unauthenticated inbound routes on the
/// listener ADR-0005 and ADR-0012 bound to loopback and called the authorisation, and it is a
/// route table this repository pins at exactly eight templates
/// (<c>Review/DecisionTests.The_board_is_the_only_way_a_human_can_change_anything</c>). What it
/// buys is a liveness probe for the dashboard, for a process the AppHost started and can
/// already see running. So the answer is no: a second inbound surface is not worth a green dot.
/// </description>
/// </item>
/// </list>
/// <para>
/// <strong>What <em>is</em> here: the meter subscription, which is the one line the factory
/// cannot do without.</strong> An OpenTelemetry <c>MeterProvider</c> does not implement
/// <see cref="System.Diagnostics.Metrics.IMeterFactory"/> — its SDK collects through its own
/// <c>MeterListener</c> and filters instruments by <em>meter name</em> — so
/// <see cref="FactoryMetrics"/> is untouched: it still takes the host's <c>IMeterFactory</c>
/// and its constructor is still exactly <c>["IMeterFactory", "Meter"]</c>, as `PolicyTests`
/// pins. The consequence is that the provider only sees a meter it was told about, and an
/// instrument on any other meter is <em>dropped with no error at all</em>. Without
/// <c>AddMeter</c> below, this factory's three instruments would simply never appear, which is
/// a silence no test and no dashboard could distinguish from a factory that did nothing.
/// <c>Observability/TelemetryPipelineTests</c> observes a real emitted measurement through the
/// production pipeline for exactly that reason.
/// </para>
/// <para>
/// <strong>What "always on" means, precisely.</strong> The SDK — providers, instrumentation and
/// the meter subscription — is registered unconditionally. The <em>exporter</em> is registered
/// only when the process has been told where to send, through
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>; that is Aspire's own design (the docs: "OTLP exporting is
/// disabled if <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> isn't configured") and it is the property that
/// makes the test host hermetic, because <c>dotnet test</c> starts the real factory several
/// hundred times and none of those runs has a collector. Under the AppHost the variable is
/// always set — DCP injects it, measured rather than assumed:
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT=https://localhost:56897</c>,
/// <c>OTEL_EXPORTER_OTLP_PROTOCOL=grpc</c>, <c>OTEL_SERVICE_NAME=agent-factory</c>, plus the
/// dashboard's API key in <c>OTEL_EXPORTER_OTLP_HEADERS</c> on a dynamically allocated HTTPS
/// port. So "always on" is a property of the <em>AppHost</em>: it always supplies a destination,
/// and the factory exports whenever one is supplied.
/// </para>
/// <para>
/// The OTel <em>logging</em> provider is registered under that same condition, and for a second
/// reason on top of the first: <c>builder.Logging.AddOpenTelemetry</c> inserts another
/// <c>ILoggerProvider</c>, and a host with more than one provider is a host whose providers are
/// not handed the external scope — which is what <c>RecordedLog</c> reads, and what the
/// work-item scope pins depend on. A provider with nowhere to send is a cost with no benefit.
/// </para>
/// </remarks>
internal static class ServiceDefaults
{
    /// <summary>
    /// Registers the telemetry pipeline on a host that is being composed.
    /// </summary>
    public static WebApplicationBuilder AddFactoryServiceDefaults(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The metrics and the traces, always: the providers exist, the framework's own signals
        // are subscribed to, and the factory's own meter is named. Nothing here reaches the
        // network — an exporter is what does that, and it is registered below when there is
        // somewhere for it to go.
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                // The one line that makes this factory's instruments exist at all. An instrument
                // on a meter the provider was not told about is dropped silently.
                .AddMeter(FactoryMetrics.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(tracing => tracing
                // No `AddSource` for a source of this factory's own: nothing in the factory
                // creates an `ActivitySource` today, and subscribing to a name that never emits
                // would be a line that looks like instrumentation and is not. The spans here are
                // the server's (the board's own requests) and the client's (the GitHub calls).
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation());

        if (!ExportsTelemetry(builder))
        {
            return builder;
        }

        // Somewhere to send. `UseOtlpExporter` reads `OTEL_EXPORTER_OTLP_*` — endpoint,
        // protocol, headers — and it is the only part of this that opens a socket, outbound, to
        // a loopback port another process listens on.
        builder.Services.AddOpenTelemetry().UseOtlpExporter();

        // And the records, on the same terms: an OTel logging provider is a second
        // `ILoggerProvider`, so it is registered when there is a destination for it rather than
        // always. See this class's own remarks for why that matters to `RecordedLog`.
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        return builder;
    }

    /// <summary>
    /// Whether this process has been told where its telemetry goes.
    /// </summary>
    /// <remarks>
    /// The same configuration key Aspire's own exporter registration reads, read the same way,
    /// so that "the exporter is on" is one fact rather than two that can disagree. The AppHost
    /// always sets it; a test host never does.
    /// </remarks>
    private static bool ExportsTelemetry(WebApplicationBuilder builder) =>
        !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
}
