namespace AgentFactory;

using AgentFactory.Clock;
using AgentFactory.Projects;
using AgentFactory.WorkItems;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// The whole factory, in one process: the config loader, the store and the board.
/// The board is an endpoint this process serves, not a service it calls (ADR-0003).
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

        // INOpenCode and IGitHub are the seams that leave the building: one call is one
        // round, and one seam covers both polling and merging. They are declared here and
        // registered by the tickets that drive them, so nothing in this process has a
        // Docker or a GitHub reference yet.

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
