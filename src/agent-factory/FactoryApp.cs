namespace AgentFactory;

using System.Diagnostics.Metrics;
using AgentFactory.Clock;
using AgentFactory.Containers;
using AgentFactory.Credentials;
using AgentFactory.Driving;
using AgentFactory.GitHub;
using AgentFactory.Loop;
using AgentFactory.Observability;
using AgentFactory.Polling;
using AgentFactory.Projects;
using AgentFactory.Results;
using AgentFactory.Rounds;
using AgentFactory.WorkItems;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;

/// <summary>
/// The whole factory, in one process: the config loader, the store, intake, the
/// orchestrator and the board. The board is an endpoint this process serves, not a
/// service it calls (ADR-0003).
/// </summary>
public static class FactoryApp
{
    /// <summary>
    /// Builds the whole factory.
    /// </summary>
    /// <param name="builder">The host being composed, already carrying anything a caller
    /// wants to substitute.</param>
    /// <param name="options">Where the project files, the store and the board live.</param>
    /// <param name="drivingTheMachine">
    /// Whether to also start the heartbeat as a hosted service. Production leaves it
    /// alone and gets a factory that moves; the test host passes false and steps the
    /// machine by hand, because a live five-second tick running alongside a test that
    /// asserts on the state of the machine makes every test in the suite a race against a
    /// timer. The driver itself is registered either way, so a test can tick it.
    /// </param>
    public static WebApplication Create(
        WebApplicationBuilder builder,
        FactoryOptions options,
        bool drivingTheMachine = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        builder.WebHost.UseUrls(options.BoardUrl.ToString().TrimEnd('/'));
        builder.Services.AddSingleton(options);

        // The real clock, unless a test has already put its own in the container.
        builder.Services.TryAddSingleton<IClock, SystemClock>();

        // The counters, on the platform's own metrics API and with no exporter. That last
        // half is the decision: this process publishes no port and binds its board to
        // loopback, and a metrics endpoint would be a second surface with a second set of
        // access rules to reach the same machine. `System.Diagnostics.Metrics` is a push
        // API — a consumer attaches to the meter in whatever process it shares — so the
        // factory can be measured without exposing anything, and *where* the numbers go is
        // the deployment's business rather than this process's.
        //
        // TryAdd, so a test can put its own in and read the counters its own factory
        // published rather than summing over the whole process.
        builder.Services.AddMetrics();
        builder.Services.TryAddSingleton(services => new FactoryMetrics(
            services.GetRequiredService<IMeterFactory>()));

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
        // The reader is the composite: environment first, the secrets directory second (#32) —
        // the directory the board's credentials form writes into, read at use time so a
        // rotation needs no restart.
        builder.Services.AddSingleton(services => new CompositeCredentialReader(
            ProcessEnvironment.The,
            services.GetRequiredService<FactoryOptions>().SecretsDirectory));
        builder.Services.TryAddSingleton<ICredentialReader>(services =>
            services.GetRequiredService<CompositeCredentialReader>());

        // The agent seam now has an implementation, so it is registered as one rather than
        // refused: a round runs in a real container from the project's configured image,
        // the OpenCode CLI is driven non-interactively inside it, and the orchestrator
        // still has no concept of a container. TryAdd, so the fake in the test host wins.
        builder.Services.TryAddSingleton<INOpenCode, WorkerRoundRunner>();

        // The GitHub seam now has an implementation, and it is the first `HttpClient` in the
        // process — deliberately, and only here. Intake reads through this client and the
        // loop merges through it, so it is the one place the factory talks to GitHub, and
        // the review surface still cannot: `PolicyTests` names this type as the single
        // component allowed to hold a transport, for the same reason it names the driver's
        // single `Task.Delay` (ADR-0003, one process, one boundary).
        //
        // The token is never on a command line and never in a URL: it goes into one
        // `Authorization` header per request and into the environment of the one git child
        // that pushes, and the loop is handed `CancellationToken.None` so that the bound on
        // a request is the client's own timeout rather than a timer the policy is forbidden
        // to have. TryAdd, so the fake in the test host wins.
        builder.Services.AddHttpClient(GitHubClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.Timeout = GitHubClient.RequestTimeout;
        });

        builder.Services.TryAddSingleton<IGitHub>(services => new GitHubClient(
            services.GetRequiredService<IHttpClientFactory>().CreateClient(GitHubClient.HttpClientName),
            services.GetRequiredService<ProjectLoadReport>(),
            options,
            services.GetRequiredService<IWorkItemStore>(),
            services.GetRequiredService<ICredentialReader>(),
            services.GetRequiredService<ILogger<GitHubClient>>()));

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
        // driven.
        builder.Services.AddSingleton<Poller>();

        // The heartbeat, and the last gap between this factory and a moving one. Every
        // prior ticket recorded the same thing: a real agent, a real container, a real
        // deriver and a bounded retry policy all exist, and nothing steps the machine on a
        // schedule, so a work item in Backlog only moved when a reviewer's click asked for
        // a step. The driver is that step, and it is safe to add now rather than earlier
        // for exactly one reason: the container budget landed with it. A timer without a
        // budget is a factory spending a worker container on every issue of every project
        // at once, which is what #8 refused.
        //
        // It is a hosted service, so the host starts and stops it, and it asks the loop for
        // a step rather than deciding anything itself — the budget is enforced where the
        // rounds are counted, in the loop, so a tick with a full budget does nothing.
        builder.Services.AddSingleton<FactoryDriver>();
        builder.Services.AddSingleton<Workspaces.ReviewWorkspaces>();
        if (drivingTheMachine)
        {
            builder.Services.AddHostedService(services => services.GetRequiredService<FactoryDriver>());
        }

        // IGitHub is one seam covering both polling and merging — one boundary rather than
        // two that can disagree about what a repository is, and now both halves of the
        // factory are wired to it: intake reads through it and the loop merges through it.
        // One `MergeAsync` is the whole of what "shipped" means to the loop: the client
        // pushes the branch, opens the pull request if there is not already one, and merges
        // it, from the host, under a credential no worker container ever held (ADR-0006).

        // The board's only write path is the reviewer's three decisions: the one form the
        // page renders, in Review. It records the decision and asks the loop to apply it,
        // so nothing else in the process can be moved by a human and the swimlane a work
        // item lands in stays the loop's answer rather than the page's.
        builder.Services.AddRazorPages();

        var app = builder.Build();

        // A workspace container outlives the process that started it: it is on the
        // daemon, and the registry that knew its port died with the process. Anything
        // still wearing this factory's workspace prefix is this factory's own and is
        // taken away here, at start, before a reviewer can be shown a link to a
        // workspace nobody holds the truth about any more.
        _ = app.Services.GetRequiredService<Workspaces.ReviewWorkspaces>()
            .SweepOrphansAsync(app.Lifetime.ApplicationStopping);

        // Resolved here rather than on the first board render, so configuration is read
        // at start the way it is meant to be.
        _ = app.Services.GetRequiredService<ProjectLoadReport>();

        // The board's stylesheet, and the only static file this process serves.
        //
        // `~/board.css` is what `Pages/Index.cshtml` links, so without this the board
        // rendered entirely unstyled — every state it distinguishes was legible, and none
        // of it looked like anything (#25).
        //
        // The file provider is named rather than left to the content root. The content root
        // is wherever the factory's own directories happen to be — a temporary directory in
        // the test host, `/app` in the deployment — and it is a factory *data* directory, so
        // it never carries a `wwwroot` of its own. The stylesheet is a property of the
        // application rather than of wherever a deployment put it, and `dotnet publish`
        // puts it beside the entry assembly, which is where this looks.
        //
        // Loopback binding is what keeps this from being a second surface: the same machine
        // that can open the board can open its stylesheet, and nothing else can reach
        // either (ADR-0005, ADR-0012). It is also the only file: no directory listing, no
        // arbitrary path, and nothing derived from a request except the name the board
        // itself renders.
        var stylesheets = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(stylesheets))
        {
            var webRoot = new PhysicalFileProvider(stylesheets);

            // Default files, so a directory address serves its own index rather than a
            // 404. The Board app is a static export under `wwwroot/admin`, and an export's
            // front door is `/admin/`, whose file is `index.html`; without this a request
            // for `/admin` finds the directory and nothing to serve. It is declared with the
            // same provider as the static files, because that provider is the published
            // directory rather than the content root, and it changes nothing for `/`: there
            // is no `wwwroot/index.html`, so the board page still answers the root as it
            // always has.
            app.UseDefaultFiles(new DefaultFilesOptions
            {
                FileProvider = webRoot,
            });

            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = webRoot,
                ServeUnknownFileTypes = false,
            });
        }
        else
        {
            // Said out loud rather than left as a silently unstyled board, which is how
            // #25 presented in the first place: a link to a file that is not there reads as
            // a board that is merely plain.
            app.Logger.LogWarning(
                "No wwwroot beside the entry assembly at {Directory}, so the board will render unstyled: "
                    + "the copy that ships is published there and this build did not put it there",
                stylesheets);
        }

        app.MapRazorPages();

        // The JSON surface the Board app renders. Most of it is a second *reading* of the
        // same state the board reads; the credentials surface is the one place a secret
        // travels, and it is write-only. Nothing here can move a work item, record a
        // decision or merge anything. The app that consumes it is a thin renderer over
        // exactly this — the factory's own judgement, serialised — so the endpoints hold
        // no policy of their own and resolve the already-registered singletons the board
        // resolves (ADR-0013, ADR-0014).
        //
        // The budget is read the way the board's own header reads it, from the loop's
        // in-flight count against the one code constant, and the mode from the options; a
        // copy kept here would be a second answer free to drift from the board's. The
        // serialiser is System.Text.Json, through `Microsoft.AspNetCore.Http.Results`, and
        // the property names are pinned on the view model rather than left to the host's
        // naming policy.
        //
        // The surface and the data share one process and one loopback binding, and the app
        // re-decides nothing: the factory's judgement is the app's only source of truth
        // (ADR-0013, ADR-0014). The endpoint resolves the store, the load report, the loop
        // and the options the board page resolves, and `BoardView.Of` puts them together;
        // it reads and writes nothing, calls no seam and moves no work item.
        var api = app.MapGroup("/api");

        api.MapGet("/board", (
            IWorkItemStore store,
            ProjectLoadReport projects,
            Orchestrator loop,
            FactoryOptions options) =>
            Microsoft.AspNetCore.Http.Results.Json(Api.BoardView.Of(store, projects, loop, options)));

        // The Projects surface (#48): the served set with its load state and the loader's
        // own refusals, and the three writes the Razor Projects page already had — add, edit
        // (one write that overwrites), and remove. Each mirrors a page handler exactly and
        // invents no capability: the file stays the single source of truth, and a write is
        // served only after a restart, which the app says where a repository is chosen. The
        // endpoint holds no validation of its own; `ProjectFiles` is the guard it calls.
        api.MapGet("/projects", (ProjectLoadReport projects) =>
            Microsoft.AspNetCore.Http.Results.Json(Api.ProjectsView.From(projects)));

        api.MapPost("/projects", (Api.ProjectWriteRequest request, FactoryOptions options) =>
        {
            var refused = ProjectFiles.Write(
                options.FactoriesDirectory,
                (request.Name ?? string.Empty).Trim(),
                (request.RepoUrl ?? string.Empty).Trim(),
                (request.WorkerImage ?? string.Empty).Trim(),
                (request.LlmModel ?? string.Empty).Trim(),
                (request.GitHubKeyName ?? string.Empty).Trim(),
                (request.LlmKeyName ?? string.Empty).Trim());

            return Microsoft.AspNetCore.Http.Results.Json(
                new Api.ProjectWriteResult(refused is null, refused),
                statusCode: refused is null
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status400BadRequest);
        });

        api.MapDelete("/projects/{name}", (string name, FactoryOptions options) =>
        {
            // The guard the page handler uses, said back as a refusal rather than a silent
            // no-op: a name the loader would never serve is not one a delete may touch.
            if (!ProjectFiles.IsServableName(name))
            {
                return Microsoft.AspNetCore.Http.Results.Json(
                    new Api.ProjectWriteResult(false, "the project name would not be a file the loader serves"),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            ProjectFiles.Delete(options.FactoriesDirectory, name);
            return Microsoft.AspNetCore.Http.Results.Json(new Api.ProjectWriteResult(true, null));
        });

        // The credential names the factory can use — the union of the keys the served
        // projects declare and the files already in the secrets directory — each with
        // whether it has a value. Presence only: the surface is write-only, and the value
        // is reached through `Has`, never `Read`, which keeps exactly its two callers.
        api.MapGet("/credentials", (
            ProjectLoadReport projects,
            FactoryOptions options,
            CompositeCredentialReader credentials) =>
            Microsoft.AspNetCore.Http.Results.Json(new Api.CredentialsView(
                CredentialFiles.Names(projects, options.SecretsDirectory)
                    .Select(name => new Api.CredentialView(name, credentials.Has(name)))
                    .ToList())));

        // A credential's value, written and never rendered back. The composite reader is
        // the boundary that turns the name into a value in the secrets directory, exactly
        // as the project's credentials form does; the answer is the name and its new
        // presence, and nothing else.
        api.MapPut("/credentials/{name}", async (
            string name,
            HttpRequest request,
            CompositeCredentialReader credentials) =>
        {
            Api.CredentialWrite? body;
            try
            {
                body = await request.ReadFromJsonAsync<Api.CredentialWrite>();
            }
            catch (System.Text.Json.JsonException)
            {
                return Microsoft.AspNetCore.Http.Results.BadRequest();
            }

            var value = body?.Value;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
            {
                return Microsoft.AspNetCore.Http.Results.BadRequest();
            }

            var written = name.Trim();
            credentials.Write(written, value.Trim());
            return Microsoft.AspNetCore.Http.Results.Json(new Api.CredentialView(written, true));
        });

        // A credential's file, removed. The name is guarded so a route segment cannot
        // reach outside the secrets directory, exactly as a project name is guarded.
        api.MapDelete("/credentials/{name}", (string name, FactoryOptions options) =>
        {
            CredentialFiles.Delete(options.SecretsDirectory, name);
            return Microsoft.AspNetCore.Http.Results.NoContent();
        });

        return app;
    }
}
