# Aspire's current shape on .NET 10

Research for [#55](https://github.com/NaniSoft/agent-factory/issues/55), a
`wayfinder:research` ticket on the map
[#54 — Wayfinder map: the factory as a Clean Architecture codebase](https://github.com/NaniSoft/agent-factory/issues/54).

Everything below was read from a primary source on **2026-10-16**. Every substantive
claim carries the URL it came from. Where a claim could not be verified from a primary
source it is marked **[unverified]** and says so rather than guessing — a guess in this
map becomes a later ticket's false premise.

The shape of this repository that the answer depends on was read from the checkout:
`src/agent-factory/FactoryApp.cs` (the composition root),
`src/agent-factory/FactoryOptions.cs`,
`src/agent-factory/Observability/FactoryMetrics.cs`,
`src/agent-factory.tests/Failures/PolicyTests.cs`,
`src/agent-factory.tests/Boundary/TheProcessTests.cs`,
`src/agent-factory.tests/Boundary/BoardPortTests.cs`, and
`src/agent-factory/GitHub/GitHubClient.cs`.

---

## 1. Aspire's current shape on .NET 10

### 1.1 The product is "Aspire", it is on 13.x, and 13.6 is the supported version

**Aspire dropped the ".NET" prefix.** The product is now called Aspire, documents live at
<https://aspire.dev>, and the .NET SDK it ships against is .NET 10.

> "The product is now called Aspire, dropping its previous .NET prefix. Aspire is a
> multi-language application platform that continues to provide best-in-class support for
> .NET applications while making Python and JavaScript first-class citizens…"
>
> **"Aspire 13 requires the .NET 10 SDK or later."**
> — <https://aspire.dev/whats-new/aspire-13>

The support policy is explicit and short
(<https://aspire.dev/support/>, last updated October 7 2026):

| Version | Original release | Latest patch | Patch released | End of support |
| --- | --- | --- | --- | --- |
| **Aspire 13.6** | 2026-09-29 | **13.6.1** | 2026-10-07 | when the next major/minor ships |
| Aspire 13.5 | 2026-08-18 | 13.5.4 | 2026-09-15 | 2026-09-29 |
| Aspire 13.4 | 2026-06-01 | 13.4.6 | 2026-06-20 | 2026-08-18 |
| Aspire 13.3 | 2026-05-07 | 13.3.5 | 2026-05-21 | 2026-06-01 |
| Aspire 13.2 | 2026-03-23 | 13.2.4 | 2026-04-24 | 2026-05-07 |
| Aspire 13.1 | 2025-12-17 | 13.1.3 | 2026-03-19 | 2026-03-23 |
| Aspire 13.0 | 2025-11-11 | 13.0.2 | 2025-12-03 | 2025-12-17 |
| **Aspire 9.5** | 2025-09-25 | **9.5.2** | 2025-10-23 | **2025-11-11** |
| Aspire 9.4 | 2025-07-29 | 9.4.2 | 2025-09-02 | 2025-09-25 |

Two claims that matter and are easy to get wrong:

- **"At any time, only the latest release of Aspire is supported by Microsoft."**
- Every Aspire 9.x version has been out of support since 2025-11-11. There is no
  supported "Aspire 9" to design against any more.

### 1.2 Does the "Aspire 9 / Aspire 13" split matter here?

**Yes, and it is not a naming detail — it changes the project file and the upgrade path.**
The version numbers jump from 9.5 straight to 13 (10, 11 and 12 were skipped) because the
versioning broke free from the .NET release cycle when the product stopped being
.NET-only. The consequence for us is the SDK-attribute form:

> "Aspire 13.0 introduces a simplified AppHost project template structure. **The SDK now
> encapsulates the `Aspire.Hosting.AppHost` package**, resulting in cleaner project files."
> — <https://aspire.dev/whats-new/aspire-13>

```xml
<!-- Aspire 9.x -->
<Project Sdk="Microsoft.NET.Sdk">
  <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.2" />
  ...
  <PackageReference Include="Aspire.Hosting.AppHost" Version="9.5.2" />
</Project>

<!-- Aspire 13.x -->
<Project Sdk="Aspire.AppHost.Sdk/13.6.1">
  ...
  <!-- no explicit Aspire.Hosting.AppHost PackageReference -->
</Project>
```

Key changes, as the docs list them
(<https://aspire.dev/whats-new/aspire-13>): the SDK is now specified directly in the
`<Project>` tag with its version; the SDK implicitly adds `Aspire.Hosting.AppHost`; the
separate `<Sdk Name="..." />` element and the `Microsoft.NET.Sdk` base SDK are removed;
the TFM moves `net9.0` → `net10.0`.

**The practical trap, and it is a version-skew trap.** Every Aspire package carries the
same number, and the `Sdk="Aspire.AppHost.Sdk/x.y.z"` attribute is *not* a
`PackageReference`, so ordinary NuGet tooling does not update it. A project can end up with
`Aspire.Hosting.*` at 13.6.1 and an SDK attribute at 13.5.4 and fail at launch with
*"Newer version of Aspire.Hosting.AppHost ... required"* — the runtime version check reads
the **SDK** version. `aspire update` fixes both; a plain `dotnet add package` fixes one.
For a repo with a `PolicyTests`-style attitude toward pinned versions, this is the thing to
pin deliberately and to have a test or a documented `aspire update` step for.

### 1.3 Exact packages and versions

| Package | Latest at time of writing | Role |
| --- | --- | --- |
| `Aspire.AppHost.Sdk` | **13.6.1** | The MSBuild SDK. `Sdk="Aspire.AppHost.Sdk/13.6.1"` in the AppHost `.csproj`. No dependencies, no framework assets — MSBuild only. |
| `Aspire.Hosting.AppHost` | **13.6.1** | The AppHost library (`DistributedApplication`, `DistributedApplicationBuilder`). **Added implicitly by the SDK** — do not also add a `PackageReference`. |
| `Aspire.Hosting` | ≥ 13.6.1 | The resource API (`AddProject`, `AddExecutable`, `AddConnectionString`, `AddParameter`, …). Transitive via `Aspire.Hosting.AppHost`. |
| `Aspire.Hosting.Testing` | **13.6.1** | Integration tests that start an AppHost. Only needed if you test the AppHost itself. |
| `Microsoft.Extensions.ServiceDiscovery` | **10.11.0** | Service discovery, in `dotnet/extensions`. Versioned on the .NET release train, not on Aspire's. |
| `Microsoft.Extensions.Http.Resilience` | **10.11.0** | Polly v8 resilience for `HttpClient`. Also `.NET`-trained. |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` | **1.17.0** | The OTLP exporter. OTel-JVM-style versioning (1.x), independent of Aspire. |
| `OpenTelemetry.Extensions.Hosting` | **1.17.0** | `AddOpenTelemetry()`, `UseOtlpExporter()`, the `TelemetryHostedService`. |

Sources: <https://www.nuget.org/packages/Aspire.AppHost.Sdk>,
<https://www.nuget.org/packages/Aspire.Hosting.AppHost>,
<https://www.nuget.org/packages/Aspire.Hosting.Testing>,
<https://www.nuget.org/packages/Microsoft.Extensions.ServiceDiscovery>,
<https://www.nuget.org/packages/Microsoft.Extensions.Http.Resilience>. The `1.17.0` /
`10.0.12` OpenTelemetry and `Microsoft.Extensions.*` floors are read off the dependency
table on the `Aspire.Hosting.AppHost` package page.

**Two version trains, and they are not the same number.** Aspire is 13.6.1;
`Microsoft.Extensions.*` is 10.11.0; OpenTelemetry is 1.17.0. Only the first is pinned by
the AppHost SDK attribute. The other two are ordinary `PackageReference`s on the
`net10.0` train.

### 1.4 What the resource-reference API is actually called

It is `AddProject`. Unchanged in name from 9.x, but the *inputs* differ.

- **C# AppHost, project-based** (the shape a `.slnx` repo wants):
  `builder.AddProject<Projects.MyApp_Web>("web")`. The `Projects.*` type is generated by a
  source generator triggered by a plain `ProjectReference` in the AppHost `.csproj` — the
  path to the project comes from the reference, so `AddProject` takes no `.csproj` path.
  <https://aspire.dev/integrations/dotnet/project-resources>
- **Opt a `ProjectReference` out** of becoming an Aspire resource:
  `<ProjectReference Include="…csproj" IsAspireProjectResource="false" />`
  <https://aspire.dev/get-started/aspire-sdk>
- **Customise the generated type name** when two projects would collide:
  `AspireProjectMetadataTypeName="MicroService1"` on the `ProjectReference`.
- **TypeScript AppHost**: `addProject(name, '../Api/Api.csproj')` — a path, no
  `ProjectReference`, no generated type. Not relevant to a .NET-only repo but it is why
  docs disagree with each other about `AddProject`'s signature.

The APIs a single self-hosted app would actually reach for:

| Call | What it does | Source |
| --- | --- | --- |
| `AddProject<Projects.X>("name")` | registers a .NET project as a resource | [project resources](https://aspire.dev/integrations/dotnet/project-resources) |
| `.WithReference(other)` | flows connection strings + service discovery into the consumer as `ConnectionStrings__<name>` / `Services__…` | [external parameters](https://aspire.dev/fundamentals/external-parameters) |
| `.WaitFor(other)` | startup ordering, gated on the other's readiness | [app host](https://aspire.dev/get-started/app-host) |
| `.WithHttpEndpoint(...)`, `.WithExternalHttpEndpoints()` | declares a host port; DCP's proxy binds it | [project resources](https://aspire.dev/integrations/dotnet/project-resources) |
| `resource.GetEndpoint("http")` | a typed `EndpointReference` for wiring into another resource | [external parameters](https://aspire.dev/fundamentals/external-parameters) |
| `builder.AddParameter(name, secret:)` | a value resolved from `Parameters__<name>` / AppHost config, prompted in the dashboard if unset | [external parameters](https://aspire.dev/fundamentals/external-parameters) |
| `builder.AddParameterFromConfiguration(name, key)` | a parameter read from an arbitrary configuration key | same |
| `builder.AddConnectionString(name)` | a connection string whose value comes from the AppHost's `ConnectionStrings` section — **this is the "an external thing that already exists" idiom** | same |
| `builder.AddExecutable(name, exe, workdir, args…)` | run a host process as a resource (`ExecutableResource`) | [executable resources](https://aspire.dev/app-host/executable-resources/) |
| `builder.AddExternalService(name, urlParam)` | model an **already-running external service**; **experimental** (`ASPIREINTERACTION001`) | [external parameters](https://aspire.dev/fundamentals/external-parameters) |

### 1.5 What else the shape imposes on a .NET 10 repo

- **The .NET 10 SDK is required for a C# AppHost**, but the AppHost can orchestrate apps
  targeting .NET 8.0 or later.
  <https://aspire.dev/get-started/prerequisites>
- **An OCI container runtime is listed as prerequisite #2**, Docker Desktop recommended,
  Podman supported via `ASPIRE_CONTAINER_RUNTIME=podman`, Rancher Desktop
  "community reported". Nothing in the docs says it is optional, even for an AppHost with
  no container resources. **[unverified]** whether the CLI actually fails to start without
  one — that needs a local experiment on a machine with no Docker.
- **The Aspire CLI is a separate install**, `aspire` on `PATH` (install via
  <https://aspire.dev/install.sh> or `install.ps1>). The AppHost SDK can also source its
  DCP and Dashboard binaries from NuGet-restored RID-specific
  `Aspire.Hosting.Orchestration.*` / `Aspire.Dashboard.Sdk.*` packages. Setting
  `<AspireUseCliBundle>true</AspireUseCliBundle>` switches to the CLI bundle; it
  **defaults to `false`** and leaving it unset emits warning `ASPIRE010`. The docs say the
  bundle "will become the only supported source for orchestration dependencies in an
  upcoming Aspire release".
  <https://aspire.dev/get-started/aspire-sdk>
- **Coordinated build and restore.** Before resources start, Aspire groups projects into
  generated MSBuild traversal projects, builds groups serially and projects within a group
  in parallel, and **restores the group with one shared NuGet graph restore**. Because the
  group restore does not invoke each root project's `Restore` target separately, custom
  targets attached directly to `Restore` (`BeforeTargets="Restore"`,
  `AfterTargets="Restore"`) **do not run per project**. Opt out with
  `Aspire:Dotnet:RestoreProjectsIndividually=true` in the AppHost's configuration (read
  once when the build plan is created, applies to every traversal).
  <https://aspire.dev/integrations/dotnet/project-resources>
- **Launch-profile selection.** With no explicit `launchProfileName`, Aspire picks: the
  argument → `AppHost:DefaultLaunchProfileName` or `DOTNET_LAUNCH_PROFILE` → the **first
  profile in `launchSettings.json`** → no profile. It then sets `DOTNET_LAUNCH_PROFILE` on
  the launched project and passes the profile's `environmentVariables` through. And:
  *"If there's no `launchSettings.json` file or selected launch profile, project resources
  don't get HTTP or HTTPS bindings by default."*
  <https://aspire.dev/integrations/dotnet/project-resources>

---

## 2. Service defaults

### 2.1 What the thing is

**Service defaults is not a package. It is a generated project.** `dotnet new
aspire-servicedefaults -n YourApp.ServiceDefaults` scaffolds a shared class library whose
sole content is one `Extensions.cs`; every project resource in the solution references it
and calls `builder.AddServiceDefaults()`.

> "The Aspire service defaults project is specifically designed for sharing the
> *Extensions.cs* file and its functionality. Don't include other shared functionality or
> models in this project."
> — <https://aspire.dev/get-started/csharp-service-defaults/>

The template's `.csproj`, verbatim from the docs:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsAspireSharedProject>true</IsAspireSharedProject>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.ServiceDiscovery" Version="10.0.0" />
    <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.12.0" />
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.12.0" />
    <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.12.0" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Http" Version="1.12.0" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Runtime" Version="1.12.0" />
  </ItemGroup>
</Project>
```

Three consequences for a four-project Clean Architecture split:

1. **`FrameworkReference Microsoft.AspNetCore.App` is imposed by the template.**
   `AgentFactory.Domain` and `AgentFactory.Application` cannot reference this project. The
   docs offer a custom service-defaults project as the escape hatch, whose example drops
   the framework reference and the AspNetCore instrumentation.
   <https://aspire.dev/get-started/csharp-service-defaults/>
2. `IsAspireSharedProject=true` means Aspire tooling uses this project as the reference
   for every project resource it enlists — and it also *rewrites* the consuming
   `Program.cs` to add the `AddServiceDefaults()` call.
   <https://aspire.dev/get-started/csharp-service-defaults/>
3. **The versions in the template are older than the current releases** (10.0.0 vs
   10.11.0; 1.12.0 vs 1.17.0). The template is a starting point, not a floor.

### 2.2 What `AddServiceDefaults()` brings

Verbatim from <https://aspire.dev/get-started/csharp-service-defaults/>:

```csharp
public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
{
    builder.ConfigureOpenTelemetry();
    builder.AddDefaultHealthChecks();
    builder.Services.AddServiceDiscovery();

    builder.Services.ConfigureHttpClientDefaults(http =>
    {
        http.AddStandardResilienceHandler();   // Turn on resilience by default
        http.AddServiceDiscovery();            // Turn on service discovery by default
    });

    return builder;
}
```

Four things, then. Plus, separately, `app.MapDefaultEndpoints()`.

### 2.3 HTTP resilience — what `AddStandardResilienceHandler` actually does

`Microsoft.Extensions.Http.Resilience`, built on Polly v8. **It stacks five strategies**
(<https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience>):

| Order | Strategy | Defaults |
| --- | --- | --- |
| 1 | Rate limiter | Queue `0`, Permit `1_000` |
| 2 | **Total request timeout** | **30 s** |
| 3 | **Retry** | **3 attempts**, exponential backoff, jitter on, 2 s delay |
| 4 | **Circuit breaker** | failure ratio 10 %, min throughput 100, sampling 30 s, break 5 s |
| 5 | **Attempt timeout** | **10 s** |

Retry and circuit breaker handle: HTTP 5xx, HTTP 408, HTTP 429, `HttpRequestException`,
and `TimeoutRejectedException`.

Two properties that matter enormously to this factory:

- **`ConfigureHttpClientDefaults` applies to every named and typed `HttpClient`, not just
  the ones the app opts in.** The GitHub client is registered with
  `AddHttpClient(GitHubClient.HttpClientName, …)`, so it inherits all five.
- **"By default, the standard resilience handler is configured to make retries for all HTTP
  methods."** Non-idempotent GitHub calls — the merge, the issue close, opening a pull
  request — are retried. `DisableFor(HttpMethod.Post, …)` / `DisableForUnsafeHttpMethods()`
  exist to turn that off, and `RemoveAllResilienceHandlers()` is the supported way to
  clear it.

### 2.4 Health checks

```csharp
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
```

```csharp
public static WebApplication MapDefaultEndpoints(this WebApplication app)
{
    if (app.Environment.IsDevelopment())      // <-- Development only
    {
        app.MapHealthChecks("/health");
        app.MapHealthChecks("/alive", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        });
    }
    return app;
}
```

Two things to design around:

- **`MapDefaultEndpoints` maps `/health` and `/alive` only in Development.** In any other
  environment nothing is mapped and the routes simply do not exist. The template's own
  comment links `https://aka.ms/dotnet/aspire/healthchecks` and says adding these in
  non-development has security implications.
- The default `self` check is hardcoded `Healthy()`. It is a liveness probe, not a check
  that the factory can reach Temporal or GitHub.

### 2.5 OpenTelemetry

```csharp
builder.Logging.AddOpenTelemetry(logging => { … });   // IncludeFormattedMessage, IncludeScopes

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation())
    .WithTracing(tracing => tracing
        .AddSource(builder.Environment.ApplicationName)
        .AddAspNetCoreInstrumentation(tracing => tracing.Filter = /* excludes /health and /alive */)
        .AddHttpClientInstrumentation());

builder.AddOpenTelemetryExporters();
```

Note what is **not** there: **there is no `AddMeter(...)` for the app's own meter.** See
§4.2 — this is the single line the factory must add.

### 2.6 Does service defaults require an OTLP collector to be present? Does it degrade?

**No, and yes it degrades.** The exporter registration is gated on one configuration key:

```csharp
var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

if (useOtlpExporter)
{
    builder.Services.AddOpenTelemetry().UseOtlpExporter();
}
```

> "Aspire telemetry works best in environments that support OTLP. **OTLP exporting is
> disabled if `OTEL_EXPORTER_OTLP_ENDPOINT` isn't configured.**"
> — <https://aspire.dev/fundamentals/telemetry/>

So with no endpoint there is no exporter at all: the OTel SDK is still registered and
still has listeners, instruments and `ILogger` plumbing, but nothing leaves the process.
No collector is needed for the app to start, and there is no health-check coupling to one.

**What is verified and what is not:**

- Verified: the exporter is not registered without the env var; the SDK registers without
  it; the app is not expected to fail.
- **[unverified]** the runtime behaviour when the endpoint **is** configured but nothing is
  listening. The .NET OTLP exporter pairs logs with a `BatchExportProcessor` and metrics
  with a `PeriodicExportingMetricReader` (default 60 s interval, cumulative temporality)
  and reports internal conditions on the `OpenTelemetry-Exporter-OpenTelemetryProtocol`
  `EventSource` — <https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/README.md>.
  That README documents dropped-batch event IDs and retry as **experimental**
  (`OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY`), which implies non-fatal retry-by-default
  behaviour, but it does not state outright "an unreachable collector does not throw into
  your application". Treat "runs quietly, logs, drops" as the expectation and
  "does not throw" as **[unverified]** until someone runs it against a closed port.

---

## 3. Telemetry always on

### 3.1 The minimum

Two things, and only one of them is in the application:

1. **In the app:** `AddServiceDefaults()` (which registers the SDK) plus
   `.WithMetrics(m => m.AddMeter(FactoryMetrics.MeterName))`. The second is not optional
   for this factory — see §4.2.
2. **In the environment:** `OTEL_EXPORTER_OTLP_ENDPOINT` set to something. Under the
   AppHost, the DCP sets it; a deployment sets it to a collector or monitoring service.

Nothing else is required, and **there is no "Aspire telemetry on/off" switch to flip** —
the whole of the opt-in is that one configuration key. Aspire's job is to *supply* the
endpoint; the app's job is to *export* when it is present.

### 3.2 Where the exporter sends by default

Protocol and endpoint, from `OtlpExporterOptions`
(<https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/src/OpenTelemetry.Exporter.OpenTelemetryProtocol/README.md>):

- `Protocol` default is **`OtlpExportProtocol.Grpc`**.
- `Endpoint` default is **`localhost:4317`** for gRPC and **`localhost:4318`** for
  `HttpProtobuf`.
- `Compression` default is none; `TimeoutMilliseconds` is the exporter's own timeout.

Under an AppHost, the endpoint is not the OTel default — DCP overrides it. Aspire
launches resources with, and the docs name these explicitly
(<https://aspire.dev/fundamentals/telemetry/>):

```
OTEL_SERVICE_NAME=myfrontend
OTEL_RESOURCE_ATTRIBUTES=service.instance.id=1a5f9c1e-…
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318
```

plus deliberately small export intervals `OTEL_BSP_SCHEDULE_DELAY`,
`OTEL_BLRP_SCHEDULE_DELAY` and `OTEL_METRIC_EXPORT_INTERVAL` "to ensure data is quickly
available in the dashboard".

> **Doc inconsistency, recorded rather than smoothed over.** The telemetry page shows
> `http://localhost:4318` as the OTLP endpoint Aspire sets, while the dashboard
> configuration table lists the OTLP/gRPC endpoint default as
> `http://localhost:18889` and OTLP/HTTP as `http://localhost:18890`
> (<https://aspire.dev/dashboard/configuration/>), and the security page says a standalone
> dashboard with API-key auth "starts the dashboard with OTLP endpoints on port `4317`
> (gRPC) and port `4318` (HTTP)" (<https://aspire.dev/dashboard/security-considerations/>).
> The container example resolves it — `-p 4317:18889 -p 4318:18890` — i.e. the
> *conventional* OTLP host ports 4317/4318 map onto the dashboard's *internal* 18889/18890.
> **[unverified]** which of these the AppHost actually hands the resource. Do not hard-code
> a port; read `OTEL_EXPORTER_OTLP_ENDPOINT` off a running AppHost.

### 3.3 Ports and sockets — precisely what opens what

This is the question the ticket exists to answer, so it is worth being exact about
*which process* opens *which* socket.

**The dashboard.** Documented defaults (<https://aspire.dev/dashboard/configuration/>):

| Endpoint | Default | Notes |
| --- | --- | --- |
| `ASPNETCORE_URLS` (frontend) | `http://localhost:18888` | **Secured with HTTPS when the dashboard is launched by the AppHost** |
| `ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL` (OTLP/gRPC) | `http://localhost:18889` | Also HTTPS when AppHost-launched |
| `ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL` (OTLP/HTTP) | `http://localhost:18890` | **Not configured by default when launched by the AppHost** — you must set it in an `aspire.config.json` launch profile |
| `ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL` | `null` | gRPC endpoint the dashboard reads resource state from |

When the AppHost launches it, the dashboard binds **HTTPS** using the ASP.NET Core
development certificate, authenticates the **browser with a token** (the `aspire run`
output shows it printed in the URL: `Dashboard: https://localhost:17068/login?t=example`
— <https://aspire.dev/get-started/add-aspire-existing-app/>) and authenticates
**incoming telemetry with an API key**
(<https://aspire.dev/dashboard/security-considerations/>). The dashboard's ports are
allocated at run time; 18888/18889/18890 are the *standalone* defaults.

**The dashboard is a separate process.** It is launched by Aspire tooling, not by the
application, and it is not this process's socket. The application is an OTLP *client*.

**What the factory process itself opens, if service defaults are adopted:**

| Socket | Direction | Cause |
| --- | --- | --- |
| Its own board listener | inbound | Kestrel. **See §5.1** — Aspire puts a DCP proxy in front of it. |
| One outbound gRPC connection to the dashboard's OTLP endpoint | outbound | `UseOtlpExporter()`'s `OtlpTraceExporter` / `OtlpMetricExporter` / `OtlpLogExporter`. Client socket only; the default protocol is gRPC so this is `Grpc.Net.Client`, not a listener. |
| Nothing else | — | Service discovery opens nothing: with no configuration provider match, its pass-through provider resolves the service name to a `DnsEndPoint` (<https://www.nuget.org/packages/Microsoft.Extensions.ServiceDiscovery>). `Microsoft.Extensions.Http.Resilience` opens nothing. |

**No listener. That is the actual answer to "which of it opens a socket": the exporter
opens an outbound connection, and the only listener Aspire adds on this machine is the
dashboard's — which belongs to another process.**

---

## 4. The cost for this app specifically

### 4.1 What the existing pin says

`PolicyTests.The_observability_component_can_reach_nothing_the_factory_does_not_already_reach`
(`src/agent-factory.tests/Failures/PolicyTests.cs:904-936`) asserts two things:

```csharp
Assert.Equal(["IMeterFactory", "Meter"], held);

Assert.Empty(CallersOf(CallsMadeBy(typeof(FactoryApp).Assembly), "System.Net.HttpListener:..ctor"));
Assert.Empty(CallersOf(CallsMadeBy(typeof(FactoryApp).Assembly), "System.Net.Sockets.TcpListener:..ctor"));
```

**The honest reading: this pin does not mechanically break.** Both assertions are IL scans
of `typeof(FactoryApp).Assembly` — *this* assembly. Adopting service defaults adds OTel to
*other* assemblies, and this repo's code will still construct no `HttpListener` and no
`TcpListener`. `Assert.Equal(["IMeterFactory", "Meter"], held)` also still holds.

What **does** break is the pin's *stated reason*, quoted from the comment at lines 926-933:

> "…this assembly publishes no measurement over a socket of any kind — so the counters
> cannot have quietly become a second inbound surface…"

That sentence is about the **process**, and the process now makes an outbound OTLP
connection and gains a dashboard on another port. `FactoryMetrics`'s own doc comment
(§ lines 13-31) makes the same claim twice, including *"Choosing the exporter here would
have put a published HTTP port into a ticket…"*. **The decision retires; the assertions do
not.** A later ticket must change the comments deliberately and visibly, in the way this
repository does everything else, and must say what replaced the argument — because the
replacement is real: the exporter is *outbound* to a loopback endpoint on another process,
not a published port.

`The_process_holds_exactly_one_http_client_and_the_merger_is_it`
(`src/agent-factory.tests/Boundary/TheProcessTests.cs:164-191`) also survives: it reflects
over constructors **in this assembly** for an `HttpClient`/`IHttpClientFactory` parameter.
The OTel exporter's `HttpClient`s live in the OTel assembly. `Assert.Equal(["GitHubClient"],
holding)` is unaffected.

`Nothing_in_the_factory_waits_for_anything_except_the_one_thing_that_has_to` scans the same
assembly for `Task.Delay`/`Thread.Sleep`/`Timer`/`CancelAfter`. The OTel
`PeriodicExportingMetricReader` uses a timer, but in its own assembly — the same scoped-scan
argument. **No existing pin breaks; three comments become false.** Say that plainly in the
ticket that does the work.

### 4.2 `FactoryMetrics` — say this plainly

**`FactoryMetrics` needs no constructor change and no new dependency.** That is verified
from source, not assumed:

- OpenTelemetry's `MeterProvider` (public API base,
  `src/OpenTelemetry.Api/Metrics/MeterProvider.cs`) is `public class MeterProvider :
  BaseProvider` — **it does not implement `System.Diagnostics.Metrics.IMeterFactory`**.
- The SDK's internal `src/OpenTelemetry/Internal/MeterFactory.cs` is a `static class` helper
  that news up a `Meter` per assembly. It is not an `IMeterFactory`.
- The SDK's `MeterProviderSdk` (`src/OpenTelemetry/Metrics/MeterProviderSdk.cs`) collects
  by creating its **own `MeterListener`** and filtering instruments by **meter name**, from
  `MeterProviderBuilderSdk.MeterSources` — i.e. the `AddMeter(...)` list. An instrument on a
  meter the provider was not told about is dropped, and the SDK logs
  `MetricInstrumentIgnored` with the message *"Use AddMeter to add the Meter to the
  provider."*
- Therefore `IMeterFactory` still resolves to the framework `MeterFactory` registered by
  the existing `builder.Services.AddMetrics()` in `FactoryApp.Create`, and the OTel
  provider listens to the same `Meter` object by name.

**What must be added is one line, and it must go in the ServiceDefaults / Web project:**

```csharp
metrics.AddMeter(FactoryMetrics.MeterName)     // "AgentFactory"
```

Without it the dashboard will show the factory's runtime, ASP.NET Core and HttpClient
metrics and **silently omit all three of the factory's own instruments** — no error, no
failed test. (Wildcard `AddMeter("*")` is the alternative and is what Aspire's own
template's `AddSource(builder.Environment.ApplicationName)` does for traces; the explicit
name is better here because it keeps the closed tag vocabulary a *named* subscription.)

So, exactly:

| | Today | After |
| --- | --- | --- |
| `FactoryMetrics` constructors | `(IMeterFactory? = null)`, `(Meter)` | **unchanged** |
| `PolicyTests` `Assert.Equal(["IMeterFactory","Meter"], held)` | passes | **still passes** |
| `PolicyTests` two `Assert.Empty(...)` listener assertions | passes | **still passes** |
| `FactoryMetrics` class doc (the "no package reference", "nothing here opens a socket" claims) | true | **false — must be rewritten deliberately** |
| `FactoryApp.Create` | `AddMetrics()` + `TryAddSingleton(FactoryMetrics)` | gains `builder.AddServiceDefaults()` and `app.MapDefaultEndpoints()` |
| OTel meter pipeline | n/a | must gain `AddMeter("AgentFactory")` |

### 4.3 The new dependency surface

Added to the **application** (via the ServiceDefaults project):

- `OpenTelemetry.Extensions.Hosting` 1.17.0
- `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.17.0
- `OpenTelemetry.Instrumentation.AspNetCore`, `.Http`, `.Runtime` (1.12.0 in the template)
- `Microsoft.Extensions.ServiceDiscovery` 10.11.0
- `Microsoft.Extensions.Http.Resilience` 10.11.0 (→ `Microsoft.Extensions.Resilience`
  10.11.0 → `Polly.Core` 8.7.0)
- `FrameworkReference Microsoft.AspNetCore.App` (template-imposed)
- `Microsoft.Extensions.Diagnostics.HealthChecks` 10.0.12

Added to the **orchestration** (the AppHost project): `Aspire.AppHost.Sdk` 13.6.1 (which
brings `Aspire.Hosting` + `Aspire.Hosting.AppHost` 13.6.1, and those bring
`AspNetCore.HealthChecks.Uris`, `Google.Protobuf`, `Grpc.AspNetCore`,
`Grpc.Net.ClientFactory`, `Grpc.Tools`, `Hex1b`, `KubernetesClient`, `MessagePack`,
`ModelContextProtocol`, `Newtonsoft.Json`, `Semver`, `StreamJsonRpc`, `YamlDotNet` and the
RID-specific DCP/Dashboard packages), plus `Aspire.Hosting.Testing` 13.6.1 in the test
project if the AppHost is itself tested.

Added to the **machine**: the `aspire` CLI, a trusted ASP.NET Core HTTPS dev certificate,
and a persistent dashboard data directory under `ASPIRE_HOME` (default: `.aspire` in the
user profile; `Dashboard:Data:Directory`; `Dashboard:Data:PersistenceMode` defaults to
`None` standalone and the AppHost sets it to `Run`)
(<https://aspire.dev/dashboard/configuration/>).

---

## 5. Things that will bite

Ordered by how likely they are to cost a ticket.

### 5.1 The DCP proxy fights `BoardUrl`, and `BoardUrl` is a pinned port

`FactoryOptions.BoardUrl` defaults to `http://127.0.0.1:5000`,
`FactoryApp.Create` calls `builder.WebHost.UseUrls(options.BoardUrl…)`, and
`BoardPortTests.One_process_serves_the_board_on_port_5000` asserts the process answers at
exactly `http://127.0.0.1:5000/`.

Under an AppHost, **that port is the proxy's, not the app's**:

> "When you define a host port for a project resource, Aspire assigns that host port to the
> **proxy** in front of the project, not directly to the underlying service."
> "Without `WithReplicas`, **the proxy still fronts the project resource**. The host port
> stays stable, while **the project itself listens on a random internal port.**
> `ASPNETCORE_URLS` points the project at the internal port, while browsers and other
> resources continue to use the stable proxy port."
> — <https://aspire.dev/integrations/dotnet/project-resources>

Two collisions, and they need a local experiment to settle:

1. Does `UseUrls(...)` win over the `ASPNETCORE_URLS` environment variable the DCP injects,
   or lose to it? `UseUrls` writes the `urls` host setting
   (<https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.hosting.hostingabstractionswebhostbuilderextensions.useurls>)
   while `ASPNETCORE_URLS` feeds the same key through the configuration system. The
   documented precedence between them is **[unverified]** — do not guess; the two possible
   outcomes are "the app binds 5000 and the proxy cannot get it" and "the proxy binds 5000
   and the app binds a random port", and they need different fixes.
2. Even if the binding cooperates, `BoardPortTests` and every test that constructs
   `FactoryApp` with `http://127.0.0.1:0` is asserting about the wrong socket once a proxy
   is in front. The "one inbound surface on a loopback binding" argument in the no-hosted-agent rule /
 gains a hop it did not account for.

Asppire offers an escape hatch — `configure: static project => { project.ExcludeLaunchProfile =
true; project.ExcludeKestrelEndpoints = false; }` and `WithEndpoint("http", e => e.IsProxied =
false)` (<https://aspire.dev/integrations/dotnet/project-resources>) — but note the docs'
warning: *"If both `applicationUrl` and Kestrel endpoint configuration are active, the
AppHost throws an exception."*

### 5.2 `AddStandardResilienceHandler` on the GitHub client is the most dangerous single interaction

Three concrete failures, all verified from the code and the docs in this repository:

1. **A new timeout this factory never asked for, on a client that already has one.**
   `GitHubClient.RequestTimeout` is 30 s. The standard handler's **attempt timeout is 10 s**
   and its total-request timeout is 30 s. A GitHub call that used to be allowed 30 seconds
   now fails at 10. Worse, Polly throws **`TimeoutRejectedException`**, which "inherits from
   `Exception`, not the standard `TimeoutException`"
   (<https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience>). But
   `GitHubClient.AskAsync` (line 1274) catches **only**
   `OperationCanceledException when InnerException is TimeoutException` and
   `HttpRequestException`. **A `TimeoutRejectedException` would escape the seam entirely**,
   bypassing `GitHubResponse.Transport` and therefore bypassing the "classification is a
   type, declared by the component that observed it" contract the whole failure policy rests
   on. It would be classified by `Failures.Classify`'s unclassified-defaults-to-transient
   path with a Polly message rather than the factory's own words.

2. **Retries on non-idempotent GitHub calls, by default.** Merge, close-issue,
   open-pull-request are all `POST`/`PUT`. "By default, the standard resilience handler is
   configured to make retries for all HTTP methods."
   `DisableForUnsafeHttpMethods()` and `RemoveAllResilienceHandlers()` are the fixes, and
   the merger's idempotency story (`A_push_that_lands_and_a_merge_that_then_fails_does_not_
   leave_a_second_pull_request`) becomes much harder to reason about when a hidden retry
   layer sits between `MergeAsync` and the wire.

3. **Two retry policies stacked.** `Orchestrator.Approve` / `MergeRetryTests` implement a
   deliberate merge-retry policy: three attempts for transient, exactly one for permanent,
   growing backoff, counted on the work item. `AddStandardResilienceHandler` adds a second,
   invisible one — up to 3 retries per attempt with exponential backoff — plus a circuit
   breaker that can throw `BrokenCircuitException`, which is also neither of the two types
   `AskAsync` catches. **A ticket should remove the standard handler from the `github` named
   client** (`RemoveAllResilienceHandlers()`) rather than reason about two policies.

### 5.3 `MapDefaultEndpoints` adds two routes, only in Development

`/health` and `/alive` are new inbound endpoints on the same loopback listener as the
board. They do not exist outside Development, which means **the AppHost-launched dashboard
will show the resource as `Running` with no health check** unless the environment is
Development. Conversely in Development they are anonymous, unauthenticated, and answer
whether the process is alive — on a listener this repository deliberately bound to
loopback and treats local access as the authorisation.

### 5.4 The dashboard displays the factory's configuration and secrets-adjacent state

> "Data displayed in the dashboard can be sensitive. For example, configuration can include
> **secrets in environment variables**, and telemetry can include sensitive runtime data."
> — <https://aspire.dev/dashboard/security-considerations/>

The dashboard shows resource **configuration and environment variables**. This process
deliberately keeps *names* travelling and values behind `ICredentialReader`
(`PolicyTests.Only_the_round_runner_puts_a_credential_into_a_round_and_only_the_merger_resolves_one`).
The AppHost does not itself set credential values into the factory's environment — but the
dashboard will still render whatever is there, plus the `factories/` project files it read,
plus the SQLite path, plus the lifted round trees' paths. Persisted to a SQLite database
under `ASPIRE_HOME` with **no independent authorization or encryption layer**; "its security
boundary is the local file system". Worth a decision rather than a shrug.

### 5.5 Temporal: there is no official Microsoft Aspire integration

Not verified from an Aspire doc because **there is no such doc**. The Aspire integrations
gallery has no Temporal entry; the Temporal integration on
<https://temporal.io/code-exchange/infinityflow-aspire-temporal-net-aspire-integration-for-temporal>
is third-party (`InfinityFlow.Aspire.Temporal`, described as *"Aspire extension to start
the temporal cli dev server as a container or executable resource. Only container is
well-supported currently"*), as is
<https://github.com/temporal-community/temporal-aspire> (`Community.Aspire.Hosting`).
Neither is a Microsoft package and neither is version-locked to the Aspire SDK.

For an **externally-run** Temporal dev server the built-in options are all pre-existing:
`AddParameter` / `AddParameterFromConfiguration` for a plain endpoint string,
`AddConnectionString` for a value shaped like a connection string
(`FactoryOptions.TemporalEndpoint` is a plain URI today), or the experimental
`AddExternalService(name, urlParameter)` which exists precisely to model a service that is
already running and is not managed by Aspire. `AddExecutable("temporal", "temporal", ".",
"server", "start-dev", …)` is available if the AppHost should own the dev server's
lifetime. **[unverified]** the health-check/`WaitFor` story for Temporal's dev server — the
`temporal` CLI's readiness signal is not obviously an HTTP endpoint Aspire can probe.

### 5.6 SQLite as a plain file: nothing to model, and that is the point

Asppire's resource model is built on `ContainerResource`, `ExecutableResource`,
`ProjectResource` and `ParameterResource`
(<https://aspire.dev/architecture/resource-model/>). **There is no resource type for "a
file on this machine",** `WithDataVolume` is a container concern, and `Dashboard`'s own
SQLite is its business rather than a modelled resource. So the store is modelled as
nothing — which is correct and costs nothing, but it means Aspire offers **no help at all**
for the one piece of durable state this factory has. The `data/` directory, the
`rounds/<work item>/round-<n>/attempt-<k>/` tree layout and "kept after the round" are
entirely outside the model.

### 5.7 The coordinated build/restore changes how this repo builds

Aspire generates MSBuild traversal projects and restores each group **once with a shared
NuGet graph**, which means custom `Restore` hooks attached with `BeforeTargets="Restore"` /
`AfterTargets="Restore"` do not run per project. I did not find such a hook in this
repository (no `Directory.Build.props`, no `Directory.Build.targets`), but `worker/` has
hand-written shell scripts and `agent-factory.csproj` may have custom targets — **[unverified]**,
worth grepping before the first AppHost build. `Aspire:Dotnet:RestoreProjectsIndividually=true`
is the documented opt-out.

### 5.8 `dotnet test` does not need Aspire, but service defaults land in the test host anyway

`FactoryHost` builds the real factory in-process with `drivingTheMachine: false` and calls
`FactoryApp.Create`. If `AddServiceDefaults()` moves into `FactoryApp.Create`, the entire
suite gets OTel, the resilience handler, service discovery and the health endpoints —
including in tests that assert on the board's rendered output and on the port. That may be
acceptable (the exporter is inert with no `OTEL_EXPORTER_OTLP_ENDPOINT`) but it is a change
in the test host's dependency surface that has to be made once, deliberately. `Aspire.Hosting.Testing`
is only needed if a ticket wants to test the **AppHost itself**; note it "randomizes proxied
ports and **disables the dashboard by default**"
(<https://www.nuget.org/packages/Aspire.Hosting.Testing>).

### 5.9 Version churn

Aspire ships ~monthly minors and **each minor ends support of the previous one**
(<https://aspire.dev/support/>). 13.0 → 13.6 in ten months, and 13.0, 13.1, 13.2, 13.3,
13.4 and 13.5 are all already end-of-support. Pinning 13.6.1 means a decision, not a
default; and the `Sdk=` attribute has to move with the packages.

---

## 6. Summary of answers

1. **Aspire 13.6.1** is the current shape. `Aspire.AppHost.Sdk` is an **MSBuild SDK**
   (`Sdk="Aspire.AppHost.Sdk/13.6.1"`) that implicitly adds `Aspire.Hosting.AppHost`; you
   do **not** also `PackageReference` it. `AddProject<Projects.X>("name")` is the API, with
   the type generated from a `ProjectReference`. The **9/13 split matters**: 9.x is out of
   support (ended 2025-11-11) and uses a different project-file shape entirely, and the
   `Sdk=` attribute is the version that the runtime actually checks.
2. **Service defaults is a generated shared project**, not a package, and it imposes
   `FrameworkReference Microsoft.AspNetCore.App`. `AddServiceDefaults()` =
   `ConfigureOpenTelemetry()` + `AddDefaultHealthChecks()` + `AddServiceDiscovery()` +
   `ConfigureHttpClientDefaults(AddStandardResilienceHandler, AddServiceDiscovery)`. It
   **does not require a collector** — the OTLP exporter is only registered when
   `OTEL_EXPORTER_OTLP_ENDPOINT` is set, and with no endpoint the SDK runs and exports
   nothing.
3. **Telemetry always on** is: `AddServiceDefaults()` in the app,
   `OTEL_EXPORTER_OTLP_ENDPOINT` in the environment (DCP supplies it), and
   **`AddMeter("AgentFactory")`** in the OTel meter pipeline. Default protocol gRPC,
   default endpoint `localhost:4317`. Ports: dashboard frontend 18888, OTLP/gRPC 18889,
   OTLP/HTTP 18890 (standalone defaults; allocated at run time under the AppHost, HTTPS
   when AppHost-launched).
4. **`FactoryMetrics` needs no change.** OpenTelemetry does not implement `IMeterFactory`;
   the SDK collects via its own `MeterListener` filtered by meter name. The
   `IMeterFactory`/`Meter` constructor pin and both no-listener assertions in
   `PolicyTests` all still pass. What changes is three **comments** (in `FactoryMetrics`
   and `PolicyTests`) whose reasons become false, one new OTel call site, and the process
   now makes an **outbound** OTLP connection while a **separate dashboard process**
   listens. No new listener in this assembly.
5. **Bites, worst first:** the DCP proxy vs `BoardUrl`'s pinned 5000; the standard
   resilience handler silently installed on the GitHub client (10 s attempt timeout that
   `GitHubClient.AskAsync` cannot catch, retries on non-idempotent merges, a second
   retry policy); `/health` and `/alive` as new anonymous routes, Development-only; the
   dashboard rendering the factory's configuration; no official Temporal integration;
   nothing at all for a SQLite file; Aspire's coordinated restore; service defaults landing
   in the test host.

## 7. Open questions this could not settle

Listed so a later ticket does not inherit them as assumptions.

- **`UseUrls` vs `ASPNETCORE_URLS` precedence.** §5.1. Needs a local experiment.
- **Does an unreachable OTLP endpoint throw into the application?** §2.6. The docs only
  establish that no exporter is registered when the key is absent.
- **Which OTLP port does the AppHost actually inject?** §3.2. The Aspire docs are
  internally inconsistent (4317/4318 vs 18889/18890) and the container port mapping only
  explains the standalone case.
- **Does the Aspire CLI require a container runtime for an AppHost with no container
  resources?** §1.5. Prerequisite #2 reads as unconditional.
- **Does this repository have custom `Restore`-attached MSBuild targets** that Aspire's
  coordinated restore would skip? §5.7.
- **Temporal dev-server readiness under `WaitFor`.** §5.5.