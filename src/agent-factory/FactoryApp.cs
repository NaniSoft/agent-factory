namespace AgentFactory;

using AgentFactory.Clock;
using AgentFactory.Containers;
using AgentFactory.Credentials;
using AgentFactory.GitHub;
using AgentFactory.Loop;
using AgentFactory.Polling;
using AgentFactory.Projects;
using AgentFactory.Results;
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

        // The container runtime, beneath the agent seam. The Docker CLI is a process and
        // not a package: ADR-0010 names `docker cp` and `docker logs` as the two ways
        // anything crosses a round's boundary, so the CLI's own verbs are the mechanism,
        // and this seam is the one place the factory knows Docker exists.
        builder.Services.TryAddSingleton<IDockerCli, DockerCli>();
        builder.Services.AddSingleton<ContainerRuntime>();

        // The result deriver, and the one place a credential name becomes a value.
        //
        // The deriver is the whole of ADR-0011 on this side of the boundary: it reads what
        // the container observed and turns it into a result, and it has no way to ask the
        // agent anything. The credential reader is the only component that can put a value
        // rather than a name into a worker container, which is why the line between the
        // LLM key (which goes in) and the GitHub key (which does not) is one method call
        // away from being checkable rather than merely intended (ADR-0006).
        builder.Services.AddSingleton<RoundResultDeriver>();
        builder.Services.TryAddSingleton<ICredentialReader>(ProcessEnvironment.The);

        // The agent seam now has an implementation, so it is registered as one rather than
        // refused: a round runs in a real container from the project's configured image,
        // the OpenCode CLI is driven non-interactively inside it, and the orchestrator
        // still has no concept of a container. TryAdd, so the fake in the test host wins.
        builder.Services.TryAddSingleton<INOpenCode, WorkerRoundRunner>();

        // The GitHub seam is still the other one with no implementation behind it, and it
        // stays a refusal: the process has to be able to start and serve the board, and a
        // call that would leave the building has to say which ticket brings the adapter
        // rather than fail on a null or, worse, quietly do nothing.
        builder.Services.TryAddSingleton<IGitHub, GitHubNotBuiltYet>();

        builder.Services.AddSingleton<IWorkItemStore>(services =>
            new SqliteWorkItemStore(options.DatabasePath, services.GetRequiredService<IClock>()));

        // Loaded once at start, with no hot reload. A project file that fails validation
        // takes only its own project out of the rotation and is reported on the board.
        builder.Services.AddSingleton(services => ProjectFileLoader.Load(
            options.FactoriesDirectory,
            services.GetRequiredService<ILoggerFactory>().CreateLogger("agent-factory.projects")));

        // The orchestrator is the factory's own policy: it knows the store, how to ask
        // for a round, how to ask for a merge, and the clock. It has no concept of a
        // container runtime — the agent seam it asks for a round through is a component
        // that has one — and the merge is one call on the one GitHub seam, so the loop
        // learns whether a change shipped without learning what a pull request is.
        builder.Services.AddSingleton<Orchestrator>();

        // The poller is intake: it reads the open issues of the projects being served and
        // records them as work items. Like the orchestrator it is stepped rather than
        // driven, and like the orchestrator nothing steps it on a schedule. A round can now
        // really start, but nothing starts the *poller* either: there is still no GitHub
        // behind its seam, so a driver that stepped it on a timer would be stepping a
        // refusal every minute. The heartbeat belongs with the real client (#10) and the
        // container budget it would have to respect (#12), and is recorded in AGENTS.md
        // rather than left to be discovered.
        builder.Services.AddSingleton<Poller>();

        // IGitHub is one seam covering both polling and merging — one boundary rather than
        // two that can disagree about what a repository is, and now both halves of the
        // factory are wired to it: intake reads through it and the loop merges through it.
        // What is registered above is the refusal, so an approve cannot complete until the
        // real client replaces it. That is the honest outcome: the reviewer's decision is
        // recorded and the work item does not report a merge that did not happen.

        // The board's only write path is the reviewer's three decisions: the one form the
        // page renders, in Review. It records the decision and asks the loop to apply it,
        // so nothing else in the process can be moved by a human and the swimlane a work
        // item lands in stays the loop's answer rather than the page's.
        builder.Services.AddRazorPages();

        var app = builder.Build();

        // Resolved here rather than on the first board render, so configuration is read
        // at start the way it is meant to be.
        _ = app.Services.GetRequiredService<ProjectLoadReport>();

        app.MapRazorPages();
        return app;
    }
}
