namespace AgentFactory.Composition;

using AgentFactory.Application;
using AgentFactory.Application.Clock;
using AgentFactory.Application.Credentials;
using AgentFactory.Application.GitHub;
using AgentFactory.Application.Observability;
using AgentFactory.Application.Projects;
using AgentFactory.Application.Rounds;
using AgentFactory.Application.Routing;
using AgentFactory.Application.WorkItems;
using AgentFactory.Infrastructure.Clock;
using AgentFactory.Infrastructure.Credentials;
using AgentFactory.Infrastructure.GitHub;
using AgentFactory.Infrastructure.Projects;
using AgentFactory.Infrastructure.Results;
using AgentFactory.Infrastructure.Rounds;
using AgentFactory.Infrastructure.Routing;
using AgentFactory.Infrastructure.WorkItems;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Diagnostics.Metrics;

/// <summary>
/// Every port the process needs and the adapter registered behind it, in one place (#81).
/// </summary>
/// <remarks>
/// <para>
/// This is the outward half of the composition root: the inside says what it needs
/// (<c>Application</c>'s seven ports), and this is where the process says how it gets each
/// one. It is separate from <see cref="FactoryApp"/> so that the composition root reads as
/// the sequence of decisions it is — what the process is told, the seams, the policy, the
/// drivers, the routes — rather than as one long list of registrations.
/// </para>
/// <para>
/// Nothing here decides anything. Every registration is a choice of implementation, and
/// the choices the factory reasons about — which credential goes into a round, what a
/// merge is, what an outcome means — are argued at the component that makes them, which is
/// where the comments live.
/// </para>
/// </remarks>
internal static class TheSeams
{
    /// <summary>
    /// Registers the clock, the counters, and the seven ports' adapters.
    /// </summary>
    public static void AddTo(IServiceCollection services, FactoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        // The real clock, unless a test has already put its own in the container.
        services.TryAddSingleton<IClock, SystemClock>();

        // The counters, on the platform's own metrics API. **The last half of this comment
        // changed deliberately and visibly for #63**, and the change is a retirement rather
        // than a correction: it used to say this process "publishes no port and binds its
        // board to loopback", and that was the reason there was no exporter here. The map's
        // decision is full Aspire — the AppHost starts the dashboard and DCP hands the
        // process `OTEL_EXPORTER_OTLP_ENDPOINT` — so the exporter exists, in
        // `Composition/ServiceDefaults`, and it is an *outbound* OTLP connection to a loopback
        // collector rather than a metrics endpoint this process serves. What did not change is
        // that nothing here names a transport: `FactoryMetrics` is still the same instruments
        // on `System.Diagnostics.Metrics`, and the pipeline that collects them is registered
        // beside this line rather than behind it.
        //
        // TryAdd, so a test can put its own in and read the counters its own factory
        // published rather than summing over the whole process.
        services.AddMetrics();
        services.TryAddSingleton(provider => new FactoryMetrics(
            provider.GetRequiredService<IMeterFactory>()));

        // The result deriver, and the one place a credential name becomes a value.
        //
        // The deriver is the whole of ADR-0011 on this side of the boundary: it reads what
        // the round observed and turns it into a result, and it has no way to ask the
        // agent anything. The credential reader is the only component that can put a value
        // rather than a name into a round, which is why the line between the LLM key
        // (which goes in) and the GitHub key (which does not) is one method call away from
        // being checkable rather than merely intended.
        services.AddSingleton<RoundResultDeriver>();

        // The reader of a routing round's verdict, and the publisher of what it proposed.
        // One instance of each, and both stateless: the reader turns a file into a verdict
        // and the publisher turns a verdict into issues, and neither of them has anything to
        // keep between calls. They are singletons because "one reader of that file's shape"
        // is a property of the process rather than of any one call, and a per-call reader
        // would be a second reader the moment somebody injected one by hand.
        services.AddSingleton<RoutingReader>();
        services.AddSingleton<IIssuePublisher, IssuePublisher>();

        // The reader is the composite: environment first, the secrets directory second (#32) —
        // the directory the board's credentials form writes into, read at use time so a
        // rotation needs no restart.
        services.AddSingleton(provider => new CompositeCredentialReader(
            ProcessEnvironment.The,
            provider.GetRequiredService<FactoryOptions>().SecretsDirectory));
        services.TryAddSingleton<ICredentialReader>(provider =>
            provider.GetRequiredService<CompositeCredentialReader>());

        // The agent seam now has an implementation, so it is registered as one rather than
        // refused: a round runs in a fresh working tree on this machine, the OpenCode CLI
        // is driven non-interactively in it as a child process, and the orchestrator still
        // has no concept of a process. TryAdd, so the fake in the test host wins.
        services.TryAddSingleton<IProcessCli, ProcessCli>();
        services.TryAddSingleton<INOpenCode, RoundRunner>();

        // The GitHub seam now has an implementation, and it is the first `HttpClient` in the
        // process — deliberately, and only here. Intake reads through this client and the
        // merger writes through it, so it is the one place the factory talks to GitHub, and
        // the review surface still cannot: `PolicyTests` names this type as the single
        // component allowed to hold a transport, for the same reason it names the driver's
        // single `Task.Delay` (ADR-0003, one process, one boundary).
        //
        // **The merger is a second port on the same class, and that is not a second
        // boundary.** `IGitHub` is intake and the issue writes; `IMerger` is everything the
        // merge needs to ask GitHub and git, one step at a time, with the policy's plan as
        // its only input. Both are answered by this one object, so there is still one place
        // that knows what a repository is — and there is no longer a REST client that can
        // reach the store, because the merge policy resolves the work item and hands the
        // plan over. `IGitHub` is one seam covering intake and the issue writes, and
        // `IMerger` is the merge's — one boundary rather than two that can disagree about
        // what a repository is, and both halves of the factory are wired to the same client.
        //
        // The token is never on a command line and never in a URL: it goes into one
        // `Authorization` header per request and into the environment of the one git child
        // that pushes, and the loop is handed `CancellationToken.None` so that the bound on
        // a request is the client's own timeout rather than a timer the policy is forbidden
        // to have. TryAdd, so the fake in the test host wins.
        services.AddHttpClient(GitHubClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.Timeout = GitHubClient.RequestTimeout;
        });

        services.TryAddSingleton<GitHubClient>(provider => new GitHubClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(GitHubClient.HttpClientName),
            provider.GetRequiredService<ProjectLoadReport>(),
            provider.GetRequiredService<ICredentialReader>(),
            provider.GetRequiredService<ILogger<GitHubClient>>()));

        services.TryAddSingleton<IGitHub>(provider => provider.GetRequiredService<GitHubClient>());

        services.TryAddSingleton<IMerger>(provider => provider.GetRequiredService<GitHubClient>());

        // The merge policy: the order, the naming rules, and every refusal about the
        // factory's own work. It is the one component that reads the store on the merge's
        // behalf, and it is in `Application` for exactly that reason — it is policy, and the
        // adapter behind it can do nothing but answer questions about GitHub and git.
        services.TryAddSingleton<IMergePolicy>(provider => new MergePolicy(
            provider.GetRequiredService<IWorkItemStore>(),
            provider.GetRequiredService<IMerger>(),
            provider.GetRequiredService<ILogger<MergePolicy>>()));

        services.AddSingleton<IWorkItemStore>(provider =>
            new SqliteWorkItemStore(options.DatabasePath, provider.GetRequiredService<IClock>()));

        // Loaded once at start, with no hot reload. A project file that fails validation
        // takes only its own project out of the rotation and is reported on the board.
        services.AddSingleton(provider => ProjectFileLoader.Load(
            options.FactoriesDirectory,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("agent-factory.projects")));
    }
}
