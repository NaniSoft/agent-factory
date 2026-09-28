namespace AgentFactory;

using AgentFactory.Clock;
using AgentFactory.Loop;
using AgentFactory.Projects;
using AgentFactory.WorkItems;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// The whole factory, in one process: the config loader, the store, the orchestrator and
/// the board. The board is an endpoint this process serves, not a service it calls
/// (ADR-0003).
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

        // IGitHub is the other seam that leaves the building — one seam covering both
        // polling and merging — and it has no implementation yet either. The poller ticket
        // registers it, and nothing in this process has a GitHub reference until then.

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
