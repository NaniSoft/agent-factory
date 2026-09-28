namespace AgentFactory;

using AgentFactory.Clock;
using AgentFactory.GitHub;
using AgentFactory.Loop;
using AgentFactory.Polling;
using AgentFactory.Projects;
using AgentFactory.Rounds;
using AgentFactory.WorkItems;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// The whole factory, in one process: the config loader, the store, intake, the
/// orchestrator and the board. The board is an endpoint this process serves, not a
/// service it calls (ADR-0003).
/// </summary>
public static class FactoryApp
{
    public static WebApplication Create(WebApplicationBuilder builder, FactoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        builder.WebHost.UseUrls(options.BoardUrl.ToString().TrimEnd('/'));
        builder.Services.AddSingleton(options);

        // The real clock, unless a test has already put its own in the container.
        builder.Services.TryAddSingleton<IClock, SystemClock>();

        // The two seams with no implementation behind them yet. What is registered here
        // is not an implementation but a refusal: the process has to be able to start and
        // serve the board before the adapters exist, and a call that would leave the
        // building has to say which ticket brings the adapter rather than fail on a null
        // or, worse, quietly do nothing. TryAdd, so the fake in the test host wins.
        builder.Services.TryAddSingleton<INOpenCode, NOpenCodeNotBuiltYet>();
        builder.Services.TryAddSingleton<IGitHub, GitHubNotBuiltYet>();

        builder.Services.AddSingleton<IWorkItemStore>(services =>
            new SqliteWorkItemStore(options.DatabasePath, services.GetRequiredService<IClock>()));

        // Loaded once at start, with no hot reload. A project file that fails validation
        // takes only its own project out of the rotation and is reported on the board.
        builder.Services.AddSingleton(services => ProjectFileLoader.Load(
            options.FactoriesDirectory,
            services.GetRequiredService<ILoggerFactory>().CreateLogger("agent-factory.projects")));

        // The orchestrator is the factory's own policy: it knows the store, the clock, and
        // how to ask for a round. It has no concept of a container runtime, so the fake
        // agent standing in for one is the only seam between it and the outside world here.
        builder.Services.AddSingleton<Orchestrator>();

        // The poller is intake: it reads the open issues of the projects being served and
        // records them as work items. Like the orchestrator it is stepped rather than
        // driven, and like the orchestrator nothing steps it yet — there is no GitHub
        // behind the seam to step it against, so the heartbeat is the ticket that brings
        // the real client.
        builder.Services.AddSingleton<Poller>();

        // IGitHub is one seam covering both polling and merging — one boundary rather than
        // two that can disagree about what a repository is. Intake and the merger are
        // both wired to it, and it is registered above refusing; the real client replaces
        // that when the merging ticket writes it.

        // The board's only write path is the reviewer's three decisions, which arrive
        // with the decisions ticket. Until then the board reads and nothing writes.
        builder.Services.AddRazorPages();

        var app = builder.Build();

        // Resolved here rather than on the first board render, so configuration is read
        // at start the way it is meant to be.
        _ = app.Services.GetRequiredService<ProjectLoadReport>();

        app.MapRazorPages();
        return app;
    }
}
