namespace AgentFactory;

using AgentFactory.Application;
using AgentFactory.Application.Durability;
using AgentFactory.Application.Loop;
using AgentFactory.Application.Polling;
using AgentFactory.Application.Projects;
using AgentFactory.Application.WorkItems;
using AgentFactory.Composition;
using AgentFactory.Domain.WorkItems;
using AgentFactory.Infrastructure.Credentials;
using AgentFactory.Infrastructure.Projects;
using AgentFactory.OpenApi;
using Microsoft.AspNetCore.Http;

// The Board's view records and its judgement classes now live in `Application` — they are the
// model's shape rather than policy (#78). The alias keeps the composition root reading
// `Api.BoardView` while the type it names sits on the inside of the boundary.
using Api = AgentFactory.Application.Api;

/// <summary>
/// The whole factory, in one process: the config loader, the store, intake, the
/// orchestrator and the board. The board is an endpoint this process serves, not a
/// service it calls (ADR-0003).
/// </summary>
/// <remarks>
/// <para>
/// This is the composition root, and what it owns is the process: the two things a host
/// must be told before it is built (where the board is bound, and what the process is
/// configured with), the one framework surface that belongs to the process rather than to
/// a module (the API's own document), the eight route templates of the JSON surface, the
/// Board export served at the root beside them, and the two reads that must happen before
/// the process serves anything. Everything a component needs in order to do its work is a
/// module's decision, and each module is argued where it is: <c>Composition/TheSeams</c>,
/// <c>Composition/ThePolicyCore</c> and <c>Composition/TheDrivers</c>.
/// </para>
/// <para>
/// <strong>This is also the seam an AppHost will see, and it is deliberately the only
/// one.</strong> [#63](https://github.com/NaniSoft/agent-factory/issues/63) adds an Aspire
/// AppHost that composes this application and Temporal and carries telemetry, and none of
/// that lands here: the AppHost references this project and runs its <c>Program</c>, which
/// reads <c>FactorySettings</c> and calls <see cref="Create"/>. So what Web owns is the
/// process's own composition, and what the AppHost owns is the host's — the lifetime, the
/// orchestration of the two processes, and where the numbers go. Nothing added here may
/// need to know that an AppHost exists.
/// </para>
/// <para>
/// The route table is the routes as written: exactly eight templates, which
/// <c>Review/DecisionTests</c> pins verbatim, because "what a human can reach" is a claim
/// about this file and not about a framework's discovery. They stay here rather than in an
/// endpoints module because the composition root is what decides the process's surface.
/// </para>
/// </remarks>
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

        // The process's own telemetry pipeline (#63): the OTel providers, the framework's and
        // this factory's own instruments, and — when the process has been told where to send —
        // the OTLP exporter. It is here rather than in an AppHost because it is the *process's*
        // defaults: `AddFactoryServiceDefaults` is `Composition/ServiceDefaults`, and it is
        // deliberately not Aspire's own `AddServiceDefaults`, which would put the standard
        // resilience handler on the GitHub client. See that file for the whole argument.
        builder.AddFactoryServiceDefaults();

        // The API's own account of what it returns (#80), registered here and served nowhere:
        // `MapOpenApi()` is deliberately not called, so this process answers no document route
        // and the document exists only as the build artefact the Board's generation script
        // reads. `TheApiDocument.Configure` is where the one correction the endpoints cannot
        // state for themselves lives.
        builder.Services.AddOpenApi(TheApiDocument.Configure);

        // The seams and their adapters, the policy core, and the drivers: three modules,
        // each argued in its own file rather than in one long list here (#81).
        Composition.TheSeams.AddTo(builder.Services, options);
        Composition.ThePolicyCore.AddTo(builder.Services);
        Composition.TheDrivers.AddTo(builder.Services, options, drivingTheMachine);

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
            Microsoft.AspNetCore.Http.Results.Json(Api.BoardView.Of(store, projects, options, poller)))
            .Produces<Api.BoardView>();

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
        })
            // The decision answer, and the two branches that answer "not a decision at all":
            // naming the type is how the document says what this endpoint returns rather than
            // leaving an `IResult` for the generator to guess at (#78).
            .Produces<Api.DecisionResult>();

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
        })
            .Produces<Api.AcceptanceResult>();

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
                : Microsoft.AspNetCore.Http.Results.NotFound())
            .Produces<Api.WorkItemDetailView>()
            .Produces(StatusCodes.Status404NotFound);

        // The Projects surface (#48): the served set with its load state and the loader's
        // own refusals, and the three writes — add, edit (one write that overwrites), and
        // remove. Each mirrors what the board could always do and
        // invents no capability: the file stays the single source of truth, and a write is
        // served only after a restart, which the app says where a repository is chosen. The
        // endpoint holds no validation of its own; `ProjectFiles` is the guard it calls.
        api.MapGet("/projects", (ProjectLoadReport projects) =>
            Microsoft.AspNetCore.Http.Results.Json(Api.ProjectsView.From(projects)))
            .Produces<Api.ProjectsView>();

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
        })
            // One type on both statuses: a refusal is `ok:false` with the loader's own words
            // and arrives as a 400, so both branches are this record (#78).
            .Produces<Api.ProjectWriteResult>(StatusCodes.Status200OK)
            .Produces<Api.ProjectWriteResult>(StatusCodes.Status400BadRequest);

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
        })
            .Produces<Api.ProjectWriteResult>(StatusCodes.Status200OK)
            .Produces<Api.ProjectWriteResult>(StatusCodes.Status400BadRequest);

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
                    .ToList())))
            .Produces<Api.CredentialsView>();

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
        })
            .Produces<Api.CredentialView>()
            .Produces(StatusCodes.Status400BadRequest);

        // A credential's file, removed. The name is guarded so a route segment cannot
        // reach outside the secrets directory, exactly as a project name is guarded.
        api.MapDelete("/credentials/{name}", (string name, FactoryOptions options) =>
        {
            CredentialFiles.Delete(options.SecretsDirectory, name);
            return Microsoft.AspNetCore.Http.Results.NoContent();
        })
            // No body at all: the only route the document says nothing about, and saying so is
            // the point of naming it rather than letting `IResult` imply a 200 with a schema.
            .Produces(StatusCodes.Status204NoContent);

        return app;
    }
}
