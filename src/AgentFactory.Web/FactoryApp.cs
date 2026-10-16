namespace AgentFactory;

using AgentFactory.Application.Clock;
using AgentFactory.Application.Credentials;
using AgentFactory.Application.GitHub;
using AgentFactory.Application.Observability;
using AgentFactory.Application.Projects;
using AgentFactory.Application.Rounds;
using AgentFactory.Application.Routing;
using AgentFactory.Application;
using AgentFactory.Domain.Rounds;
using AgentFactory.Domain.WorkItems;
using AgentFactory.Application.Durability;
using AgentFactory.Application.Driving;
using AgentFactory.Infrastructure.GitHub;
using AgentFactory.Infrastructure.Clock;
using AgentFactory.Infrastructure.Credentials;
using AgentFactory.Infrastructure.Projects;
using AgentFactory.Infrastructure.Results;
using AgentFactory.Infrastructure.Rounds;
using AgentFactory.Infrastructure.Routing;
using AgentFactory.Application.Loop;
using AgentFactory.Application.Polling;
using AgentFactory.WorkItems;
using AgentFactory.Application.WorkItems;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Diagnostics.Metrics;

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

        // The result deriver, and the one place a credential name becomes a value.
        //
        // The deriver is the whole of ADR-0011 on this side of the boundary: it reads what
        // the round observed and turns it into a result, and it has no way to ask the
        // agent anything. The credential reader is the only component that can put a value
        // rather than a name into a round, which is why the line between the LLM key
        // (which goes in) and the GitHub key (which does not) is one method call away from
        // being checkable rather than merely intended (ADR-0006).
        builder.Services.AddSingleton<RoundResultDeriver>();

        // The reader of a routing round's verdict, and the publisher of what it proposed.
        // One instance of each, and both stateless: the reader turns a file into a verdict
        // and the publisher turns a verdict into issues, and neither of them has anything to
        // keep between calls. They are singletons because "one reader of that file's shape"
        // is a property of the process rather than of any one call, and a per-call reader
        // would be a second reader the moment somebody injected one by hand (ADR-0015).
        builder.Services.AddSingleton<RoutingReader>();
        builder.Services.AddSingleton<IIssuePublisher, IssuePublisher>();
        // The reader is the composite: environment first, the secrets directory second (#32) —
        // the directory the board's credentials form writes into, read at use time so a
        // rotation needs no restart.
        builder.Services.AddSingleton(services => new CompositeCredentialReader(
            ProcessEnvironment.The,
            services.GetRequiredService<FactoryOptions>().SecretsDirectory));
        builder.Services.TryAddSingleton<ICredentialReader>(services =>
            services.GetRequiredService<CompositeCredentialReader>());

        // The agent seam now has an implementation, so it is registered as one rather than
        // refused: a round runs in a fresh working tree on this machine, the OpenCode CLI
        // is driven non-interactively in it as a child process, and the orchestrator still
        // has no concept of a process. TryAdd, so the fake in the test host wins.
        builder.Services.TryAddSingleton<IProcessCli, ProcessCli>();
        builder.Services.TryAddSingleton<INOpenCode, RoundRunner>();

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
        // plan over.
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

        builder.Services.TryAddSingleton<GitHubClient>(services => new GitHubClient(
            services.GetRequiredService<IHttpClientFactory>().CreateClient(GitHubClient.HttpClientName),
            services.GetRequiredService<ProjectLoadReport>(),
            services.GetRequiredService<ICredentialReader>(),
            services.GetRequiredService<ILogger<GitHubClient>>()));

        builder.Services.TryAddSingleton<IGitHub>(services => services.GetRequiredService<GitHubClient>());

        builder.Services.TryAddSingleton<IMerger>(services => services.GetRequiredService<GitHubClient>());

        // The merge policy: the order, the naming rules, and every refusal about the
        // factory's own work. It is the one component that reads the store on the merge's
        // behalf, and it is in `Application` for exactly that reason — it is policy, and the
        // adapter behind it can do nothing but answer questions about GitHub and git.
        builder.Services.TryAddSingleton<IMergePolicy>(services => new MergePolicy(
            services.GetRequiredService<IWorkItemStore>(),
            services.GetRequiredService<IMerger>(),
            services.GetRequiredService<ILogger<MergePolicy>>()));

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
        // prior ticket recorded the same thing: a real agent, a real deriver and a bounded
        // retry policy all exist, and nothing steps the machine on a schedule, so a work
        // item in Backlog only moved when a reviewer's click asked for a step. The driver
        // is that step, and it is safe to add now rather than earlier for exactly one
        // reason: the round concurrency landed with it. A timer with no ceiling behind it
        // is a factory spending a round on every issue of every project at once, which is
        // what #8 refused.
        //
        // It is a hosted service, so the host starts and stops it, and it asks the loop for
        // a step rather than deciding anything itself — the concurrency is enforced where
        // the rounds are counted, in the loop, so a tick at the ceiling does nothing.
        builder.Services.AddSingleton<FactoryDriver>();
        if (drivingTheMachine)
        {
            builder.Services.AddHostedService(services => services.GetRequiredService<FactoryDriver>());
        }

        // IGitHub is one seam covering intake and the issue writes, and `IMerger` is the
        // merge's — one boundary rather than two that can disagree about what a repository
        // is, and both halves of the factory are wired to the same client. One
        // `MergeAsync` on the policy is the whole of what "shipped" means to the loop: it
        // pushes the branch, opens the pull request if there is not already one, and merges
        // it, from the host, under a credential no worker container ever held (ADR-0006).

        // The durable workflow, when a Temporal endpoint is configured. One workflow per
        // ticket: its history is the record of where the ticket was, a round in flight
        // survives a restart of this process, and the board's clicks reach the workflow as
        // updates. The hosted service hosts the worker and reconnects on its own, so a
        // Temporal outage is a degraded board rather than a dead factory. Without an
        // endpoint the factory runs in store-only mode exactly as it always did, and the
        // starter that is not there answers every call as a no-op — which is also what a
        // test host gets, unless it substitutes its own.
        if (!string.IsNullOrWhiteSpace(options.TemporalEndpoint))
        {
            builder.Services.AddSingleton<ITicketStarter, Durability.TemporalTickets>();
            builder.Services.AddHostedService(services =>
                (Durability.TemporalTickets)services.GetRequiredService<ITicketStarter>());
        }
        else
        {
            builder.Services.TryAddSingleton<ITicketStarter, Durability.NullTicketStarter>();
        }

        var app = builder.Build();

        // The same is true of a round the process died inside, and this is the half that
        // makes a restart survivable: the pending-round row says which round was in
        // flight, the row is cleared, and the work item goes back where a fresh round is
        // asked for it. Synchronous and before serving, so a reconcile that is half-done
        // is a state the heartbeat can never observe.
        app.Services.GetRequiredService<Orchestrator>().RecoverOrphanedRounds();

        // Resolved here rather than on the first board render, so configuration is read
        // at start the way it is meant to be.
        _ = app.Services.GetRequiredService<ProjectLoadReport>();

        // The Board app: the static Prism export this process serves at the root, beside the
        // JSON surface under `/api`. It is the human surface now — the app's routes are the
        // pages, and the only write path is `POST /api/work-items/{id}/decisions` — and the
        // one thing the process serves that is not the app is this `wwwroot` itself.
        // `BoardExport` is where the serving lives, and where the two cases the framework's
        // own handlers do not cover — a route without its trailing slash, and the boundary
        // `/api` must never cross — are argued.
        //
        // Loopback binding is what keeps this from being a second surface: the same machine
        // that can open the Board can reach the app and its `/api` reads, and nothing else
        // can reach either (ADR-0005, ADR-0012).
        BoardExport.Serve(app, Path.Combine(AppContext.BaseDirectory, "wwwroot"));

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
            FactoryOptions options,
            Poller poller) =>
            Microsoft.AspNetCore.Http.Results.Json(Api.BoardView.Of(store, projects, options, poller)));

        // A reviewer's decision, the board's one write path now that the Razor form is gone
        // (#45, #51). It invents no capability: `Decisions.TryParse`, then
        // `IWorkItemStore.RecordDecision`, then
        // `Orchestrator.StepAsync` — and it moves no work item and merges nothing itself,
        // because the lane a decision means is the loop's answer and the loop is the only
        // thing here that talks to GitHub. A fourth decision, a request for changes with
        // no words, a work item outside the decidable lanes and an approval the merge could
        // not carry out all come back as `applied:false` with the factory's own words in
        // `refusal`, so a refusal is rendered where the reviewer acted rather than silently.
        api.MapPost("/work-items/{id}/decisions", async (
            string id,
            Api.DecisionRequest request,
            IWorkItemStore store,
            Orchestrator loop,
            ITicketStarter tickets) =>
        {
            string Lane()
            {
                return Guid.TryParse(id, out var candidate) && store.Get(candidate) is { } workItem
                    ? workItem.Swimlane.ToString()
                    : string.Empty;
            }

            // A work item that is not one, or a slug that is not one of the three — including
            // a form that sent no slug at all, which arrives here as a null — is not a
            // decision: nothing here parses an arbitrary name into the set, so a form posted
            // by hand is refused rather than interpreted and the set cannot grow by accident.
            var posted = request.Decision;
            if (!Guid.TryParse(id, out var workItemId)
                || posted is null
                || !Decisions.TryParse(posted, out var made))
            {
                return Microsoft.AspNetCore.Http.Results.Json(new Api.DecisionResult(
                    false,
                    "That is not one of the three decisions. A work item in Review is "
                        + "approved, sent back for changes, or rejected, and there is nothing else to decide.",
                    Lane()));
            }

            // A ticket whose story is a durable workflow is answered by the workflow, as an
            // update: the decision is recorded by the workflow's own activity, the lane it
            // means is applied there, and the answer comes back once it has — so the board's
            // click still says applied or refused in its own words. A ticket the workflow
            // never owned — one from before the endpoint was configured — falls through to
            // the store and the loop, exactly as it always went.
            if (tickets.Configured)
            {
                var (handled, applied, refusal) = await tickets.DecideAsync(
                    workItemId,
                    posted,
                    request.Feedback);
                if (handled)
                {
                    return Microsoft.AspNetCore.Http.Results.Json(new Api.DecisionResult(applied, refusal, Lane()));
                }
            }

            try
            {
                // The store is the component that knows which lane the work item is in and
                // what it made of the feedback, so its objection is the reviewer's answer in
                // its own words. Anything that is not a refusal is a fault and is left to fail
                // rather than dressed up as a decision that was declined.
                store.RecordDecision(workItemId, made, request.Feedback);
            }
            catch (Exception refused) when (refused is KeyNotFoundException or InvalidOperationException)
            {
                return Microsoft.AspNetCore.Http.Results.Json(
                    new Api.DecisionResult(false, refused.Message, Lane()));
            }

            // The decision is recorded and the lane it means is the loop's, so this asks the
            // loop rather than moving the work item: one step applies one transition, and a
            // decision comes before everything else a step could apply. A refusal here is an
            // approval whose merge did not land — on the record, parked, and said rather than
            // silent.
            var step = await loop.StepAsync();

            return Microsoft.AspNetCore.Http.Results.Json(
                new Api.DecisionResult(step.Applied, step.Refusal, Lane()));
        });

        // The acceptance gate (#40, restored by #52): intake puts every issue in Backlog and
        // nothing is built until a reviewer accepts it. This mirrors the deleted page's
        // `OnPostBuildAsync` exactly — `Orchestrator.PromoteAsync` is the same component the
        // loop uses to apply every other transition, and it moves no work item and spends no
        // container itself. A work item not waiting in Backlog is refused with the factory's
        // own words in `refusal`, so the app renders the refusal where the reviewer acted
        // rather than leaving them wondering whether the click was lost.
        api.MapPost("/work-items/{id}/accept", async (
            string id,
            Orchestrator loop,
            ITicketStarter tickets) =>
        {
            if (!Guid.TryParse(id, out var workItemId))
            {
                return Microsoft.AspNetCore.Http.Results.Json(new Api.AcceptanceResult(
                    Applied: false,
                    Refusal: "That work item is not waiting in Backlog, so there is nothing to accept into the build."));
            }

            // A ticket whose story is a durable workflow is accepted by the workflow, as an
            // update: the acceptance is durable, and the build it admits begins from the
            // workflow's own history. A ticket the workflow never owned falls through to
            // the loop, exactly as it always went.
            if (tickets.Configured)
            {
                var (handled, applied, refusal) = await tickets.AcceptAsync(workItemId);
                if (handled)
                {
                    return Microsoft.AspNetCore.Http.Results.Json(new Api.AcceptanceResult(
                        Applied: applied,
                        Refusal: refusal ?? (applied
                            ? null
                            : "That work item is not waiting in Backlog, so there is nothing to accept into the build.")));
                }
            }

            var promoted = await loop.PromoteAsync(workItemId);
            return Microsoft.AspNetCore.Http.Results.Json(new Api.AcceptanceResult(
                Applied: promoted,
                Refusal: promoted
                    ? null
                    : "That work item is not waiting in Backlog, so there is nothing to accept into the build."));
        });

        // The work-item detail (#46): the whole record behind a board card — the work item,
        // every round it has run with the host's diff, the decisions made and the decisions
        // still offered, and how it ended. It is a read and nothing else:
        // `WorkItemDetailView.Of` puts the store's own rows together with the factory's own
        // judgement (`HowToReadTheDiff`, `HowItEnded`, `Decisions.OfferedIn`), and the
        // endpoint holds no policy of its own. An id the store has never seen is a 404
        // rather than an empty record, because "no such work item" and "a work item with
        // nothing on it" are different answers.
        api.MapGet("/work-items/{id}", (string id, IWorkItemStore store) =>
            Guid.TryParse(id, out var workItemId) && store.Get(workItemId) is { } workItem
                ? Microsoft.AspNetCore.Http.Results.Json(Api.WorkItemDetailView.Of(workItem, store))
                : Microsoft.AspNetCore.Http.Results.NotFound());

        // The Projects surface (#48): the served set with its load state and the loader's
        // own refusals, and the three writes — add, edit (one write that overwrites), and
        // remove. Each mirrors what the board could always do and
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
        // whether it has a value; beside them, the LLM providers a model reference may
        // name and the canonical variable each reads, straight off the factory's own
        // table so the app renders the fact rather than carrying a copy. Presence only:
        // the surface is write-only, and the value is reached through `Has`, never
        // `Read`, which keeps exactly its two callers.
        api.MapGet("/credentials", (
            ProjectLoadReport projects,
            FactoryOptions options,
            CompositeCredentialReader credentials) =>
            Microsoft.AspNetCore.Http.Results.Json(new Api.CredentialsView(
                CredentialFiles.Names(projects, options.SecretsDirectory)
                    .Select(name => new Api.CredentialView(name, credentials.Has(name)))
                    .ToList(),
                LlmProviders.OrderedIds
                    .Select(id => new Api.LlmProviderView(
                        id,
                        LlmProviders.CanonicalEnvironmentVariables[id]))
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

            // The name is guarded the same way a delete's is: it arrives from a route
            // segment, so a name that would not be a file the factory serves is refused
            // rather than handed to a write that would create a path outside the secrets
            // directory. The delete below already guards; the write must too.
            var written = (name ?? string.Empty).Trim();
            if (!CredentialFiles.IsServableName(written) || string.IsNullOrWhiteSpace(value))
            {
                return Microsoft.AspNetCore.Http.Results.BadRequest();
            }

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
