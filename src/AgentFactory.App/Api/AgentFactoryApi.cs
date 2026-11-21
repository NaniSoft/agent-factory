using AgentFactory.App.Fleet;

namespace AgentFactory.App.Api;

/// <summary>
/// The fleet's JSON surface, written down in one place. The React app beside
/// this process is a renderer over exactly these reads and commands: the
/// endpoints resolve the same singletons the fleet loop drives, and they hold
/// no policy of their own.
/// </summary>
public static class AgentFactoryApi
{
    public static void MapAgentFactoryApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // ---- the fleet ---------------------------------------------------------

        api.MapGet("/fleet", async (DashboardService dashboard, ConfigStore config, FleetOptions options) =>
        {
            var (port, pollSeconds) = dashboard.GetFleetInfo();
            var up = await dashboard.EngineUpAsync();
            var runs = dashboard.GetRuns();
            return Results.Json(new FleetView(
                "Agent Factory",
                "An autonomous agent fleet: GitHub issues in, reviewed pull requests out, with a human reviewing the pull requests.",
                new EngineView(up, port),
                pollSeconds,
                dashboard.GetTargetCount(),
                options.OptInLabel,
                config.IsPaused(),
                FleetViews.Counts(runs),
                DateTimeOffset.UtcNow));
        });

        api.MapPost("/fleet/pause", (PauseRequest request, ConfigStore config, FleetRunner runner) =>
        {
            config.SetPaused(request.Paused);
            runner.Wake();
            return Results.Json(new PauseResult(config.IsPaused()));
        });

        // ---- runs --------------------------------------------------------------

        api.MapGet("/runs", (DashboardService dashboard) =>
            Results.Json(dashboard.GetRuns().Select(FleetViews.Summary).ToList()));

        api.MapGet("/runs/{runId}", async (string runId, int? phase, DashboardService dashboard) =>
        {
            var run = dashboard.GetRun(runId);
            if (run is null)
            {
                return Results.NotFound();
            }

            var children = dashboard.GetRuns()
                .Where(r => r.ParentRunId == run.RunId)
                .OrderBy(r => r.ChildOrder)
                .Select(FleetViews.Summary)
                .ToList();
            var parent = run.ParentRunId is { } parentId ? dashboard.GetRun(parentId) : null;

            var selectedPhaseIndex = run.Phases.Count == 0
                ? 0
                : Math.Clamp(phase ?? run.Phases.Count - 1, 0, run.Phases.Count - 1);
            var selectedPhase = run.Phases.Count == 0 ? null : run.Phases[selectedPhaseIndex];
            var sessionId = selectedPhase?.SessionId ?? run.SessionId;
            var session = await dashboard.GetSessionAsync(sessionId);
            var transcript = await dashboard.GetTranscriptAsync(sessionId);

            return Results.Json(new RunDetailView(
                FleetViews.Summary(run),
                run.IssueBody,
                run.AgentSummary,
                run.Phases.Select(FleetViews.Phase).ToList(),
                dashboard.GetLogTail(run.RunId),
                children,
                parent is null ? null : FleetViews.Summary(parent),
                FleetViews.Session(session),
                transcript?.Select(line => new TranscriptLineView(line.Time, line.Role, line.Text)).ToList() ?? [],
                selectedPhaseIndex,
                sessionId));
        });

        api.MapGet("/runs/{runId}/transcript", async (string runId, int? phase, DashboardService dashboard) =>
        {
            var run = dashboard.GetRun(runId);
            if (run is null)
            {
                return Results.NotFound();
            }

            var selectedPhaseIndex = run.Phases.Count == 0
                ? 0
                : Math.Clamp(phase ?? run.Phases.Count - 1, 0, run.Phases.Count - 1);
            var selectedPhase = run.Phases.Count == 0 ? null : run.Phases[selectedPhaseIndex];
            var sessionId = selectedPhase?.SessionId ?? run.SessionId;
            var session = await dashboard.GetSessionAsync(sessionId);
            var transcript = await dashboard.GetTranscriptAsync(sessionId);

            return Results.Json(new TranscriptView(
                selectedPhaseIndex,
                sessionId,
                FleetViews.Session(session),
                transcript?.Select(line => new TranscriptLineView(line.Time, line.Role, line.Text)).ToList() ?? []));
        });

        // ---- pipelines ---------------------------------------------------------

        api.MapGet("/pipelines", (ConfigStore config) => Results.Json(PipelineCatalog(config)));

        api.MapPost("/pipelines", (PipelineDefinition pipeline, ConfigStore config) =>
        {
            PipelineRules.Normalize(pipeline);
            var profiles = config.Profiles();
            var skills = SkillLibrary.NamesOnDisk();
            PipelineRules.EnsureId(pipeline, config.Pipelines());

            var errors = PipelineRules.Validate(pipeline, profiles, skills);
            if (errors.Count > 0)
            {
                return Results.Json(new SavePipelineResult(false, errors, pipeline.Id), statusCode: 400);
            }

            config.SavePipeline(pipeline);
            return Results.Json(new SavePipelineResult(true, [], pipeline.Id));
        });

        api.MapPost("/pipelines/{id}/default", (string id, ConfigStore config) =>
        {
            try
            {
                config.SetDefaultPipeline(id);
                return Results.Json(new CommandResult(true, null));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new CommandResult(false, ex.Message), statusCode: 400);
            }
        });

        api.MapDelete("/pipelines/{id}", (string id, ConfigStore config) =>
        {
            var (ok, error) = config.DeletePipeline(id);
            return ok
                ? Results.Json(new CommandResult(true, null))
                : Results.Json(new CommandResult(false, error), statusCode: 400);
        });

        // ---- profiles ----------------------------------------------------------

        api.MapGet("/profiles", (ConfigStore config) => Results.Json(ProfileCatalog(config)));

        api.MapGet("/engine/catalog", async (OpenCodeEngine engine) =>
        {
            var providers = await engine.GetConfigProvidersAsync();
            return Results.Json(new CatalogView(
                providers is not null,
                providers?.Select(p => new ProviderView(p.Id, p.Name, p.Models)).ToList() ?? []));
        });

        api.MapGet("/engine/catalog/external", async (CancellationToken ct) =>
        {
            var models = await FlavourFacts.FetchExternalModelsAsync(AgentFlavour.OpencodeGo, ct);
            return Results.Json(new CatalogView(
                models.Count > 0,
                models.Count > 0
                    ? [new ProviderView("opencode-go", "OpenCode Go", models)]
                    : []));
        });

        api.MapPost("/profiles", (ProfileSaveRequest request, ConfigStore config) =>
        {
            if (!Enum.TryParse<AgentFlavour>(request.Flavour, ignoreCase: true, out var flavour))
            {
                return Results.Json(
                    new ProfileSaveResult(false, [$"unknown flavour '{request.Flavour}'"], null),
                    statusCode: 400);
            }

            var profile = request.Id is { Length: > 0 } id
                ? config.ProfileFor(id) ?? new AgentProfile { Id = id }
                : new AgentProfile();
            profile.Name = (request.Name ?? string.Empty).Trim();
            profile.Flavour = flavour;
            if (request.Models is { } models)
            {
                profile.Models = [.. models.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.Ordinal)];
            }
            if (request.ClearKey)
            {
                profile.ApiKey = string.Empty;
            }
            else if (!string.IsNullOrWhiteSpace(request.ApiKey))
            {
                profile.ApiKey = request.ApiKey.Trim();
            }
            if (profile.Id.Length == 0)
            {
                profile.Id = PipelineRules.ProfileId(profile.Name, config.Profiles().Select(p => p.Id));
            }

            var errors = new List<string>();
            if (profile.Name.Length == 0)
            {
                errors.Add("Name is empty");
            }
            if (profile.Models.Count == 0)
            {
                errors.Add($"Palette is empty — no phase could pin '{profile.Name}'.");
            }
            if (errors.Count > 0)
            {
                return Results.Json(new ProfileSaveResult(false, errors, null), statusCode: 400);
            }

            config.SaveProfile(profile);
            return Results.Json(new ProfileSaveResult(true, [], FleetViews.ProfileForPage(profile)));
        });

        api.MapDelete("/profiles/{id}", (string id, ConfigStore config) =>
        {
            var (ok, error) = config.DeleteProfile(id);
            return ok
                ? Results.Json(new CommandResult(true, null))
                : Results.Json(new CommandResult(false, error), statusCode: 400);
        });

        api.MapPut("/profiles/utility", (BindingRequest request, ConfigStore config) =>
            SetBinding(request, fallback: false, config));

        api.MapPut("/profiles/fallback", (BindingRequest request, ConfigStore config) =>
            SetBinding(request, fallback: true, config));

        api.MapPost("/profiles/probe", (ProbeRequest request, ConfigStore config, OpenCodeEngine engine, CancellationToken ct) =>
            ProbeAsync(request, config, engine, ct));

        api.MapPost("/profiles/verify", (ProfileVerifyRequest request, ConfigStore config, OpenCodeEngine engine, FleetOptions options, CancellationToken ct) =>
            VerifyProfileAsync(request, config, engine, options, ct));

        api.MapGet("/fleet/github", (ConfigStore config) =>
        {
            var effective = config.EffectiveGithubToken();
            var stored = config.HasStoredGithubToken();
            string preview = "";
            if (!string.IsNullOrWhiteSpace(effective))
            {
                var t = effective.Trim();
                preview = t.Length <= 8 ? "••••" : t[..4] + "••••" + t[^4..];
            }
            return Results.Json(new GithubTokenView(stored, !string.IsNullOrWhiteSpace(effective), stored ? "stored (UI)" : (string.IsNullOrWhiteSpace(effective) ? "none" : "secret/env"), string.IsNullOrWhiteSpace(preview) ? null : preview));
        });

        api.MapPost("/fleet/github", (GithubTokenSaveRequest request, ConfigStore config, FleetRunner runner) =>
        {
            config.SetGithubToken(request.Token);
            runner.Wake();
            var effective = config.EffectiveGithubToken();
            string preview = "";
            if (!string.IsNullOrWhiteSpace(effective))
            {
                var t = effective.Trim();
                preview = t.Length <= 8 ? "••••" : t[..4] + "••••" + t[^4..];
            }
            return Results.Json(new GithubTokenView(config.HasStoredGithubToken(), !string.IsNullOrWhiteSpace(effective), config.HasStoredGithubToken() ? "stored (UI)" : (string.IsNullOrWhiteSpace(effective) ? "none" : "secret/env"), string.IsNullOrWhiteSpace(preview) ? null : preview));
        });

        api.MapPost("/fleet/github/verify", (GithubVerifyRequest request, ConfigStore config, FleetOptions options, CancellationToken ct) =>
            VerifyGithubAsync(request, config, options, ct));

        api.MapPost("/engine/restart", async (OpenCodeEngine engine, CancellationToken ct) =>
        {
            try
            {
                await engine.RestartAsync(ct);
                return Results.Json(new CommandResult(true, null));
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or OperationCanceledException)
            {
                return Results.Json(new CommandResult(false, $"serve restart failed: {ex.Message}"), statusCode: 500);
            }
        });

        // ---- targets and orgs --------------------------------------------------

        api.MapGet("/targets", (
            ConfigStore config,
            FleetStatusService status,
            DashboardService dashboard,
            OrgScanner orgs) =>
        {
            var targets = config.Targets();
            var orgList = config.Orgs();
            var runs = dashboard.GetRuns();

            var lastPoll = targets
                .Select(t => (t.Repo, Poll: status.LastPoll(t.Repo)))
                .Where(pair => pair.Poll is not null)
                .ToDictionary(
                    pair => pair.Repo,
                    pair => new TargetPollView(pair.Poll!.Value.At, pair.Poll.Value.Note));
            var queued = targets.ToDictionary(
                t => t.Repo,
                t => runs.Count(r => r.Repo == t.Repo && r.State == RunState.Queued && r.Kind != RunKind.Child));
            var scans = orgList.ToDictionary(o => o.Org, o => ToScanView(orgs.Snapshot(o.Org)));

            return Results.Json(new TargetsView(
                targets,
                orgList,
                config.Pipelines().Select(p => new PipelineChoiceView(p.Id, p.Name)).ToList(),
                config.CurrentDefaultPipelineId(),
                lastPoll,
                queued,
                scans));
        });

        api.MapPost("/targets/enroll", async (
            EnrollRequest request,
            ConfigStore config,
            GitHubService github,
            FleetRunner runner,
            CancellationToken ct) =>
        {
            var name = (request.Name ?? string.Empty).Trim().TrimEnd('/')
                .Replace("https://github.com/", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("http://github.com/", string.Empty, StringComparison.OrdinalIgnoreCase);
            if (name.Length == 0 || name.Contains(' '))
            {
                return Results.Json(new EnrollResult(false, "Enter an owner/name (repo) or an org login.", null), statusCode: 400);
            }

            try
            {
                if (string.Equals(request.Kind, "org", StringComparison.OrdinalIgnoreCase))
                {
                    if (config.Orgs().Any(o => string.Equals(o.Org, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        return Results.Json(new EnrollResult(false, "org already enrolled.", null), statusCode: 400);
                    }
                    if (!await github.OrgExistsAsync(name, ct))
                    {
                        return Results.Json(new EnrollResult(false, $"gh can't list org {name} — check the login and access.", null), statusCode: 400);
                    }
                    config.AddOrg(new OrgRecord { Org = name, Intake = IntakeRules.Label, Enabled = true });
                    runner.Wake();
                    return Results.Json(new EnrollResult(true, null, $"org {name} enrolled — first scan running…"));
                }

                if (name.Count(c => c == '/') != 1)
                {
                    return Results.Json(new EnrollResult(false, "A repo enrollment is owner/name — one slash.", null), statusCode: 400);
                }
                if (config.Targets().FirstOrDefault(t => string.Equals(t.Repo, name, StringComparison.OrdinalIgnoreCase)) is { } duplicate)
                {
                    var where = duplicate.Org is { } via
                        ? $"already enrolled via org {via} (most-specific-wins) — edit its target row instead"
                        : "already enrolled.";
                    return Results.Json(new EnrollResult(false, where, null), statusCode: 400);
                }
                if (!await github.RepoExistsAsync(name, ct))
                {
                    return Results.Json(new EnrollResult(false, $"gh can't see {name} — check the name and access.", null), statusCode: 400);
                }
                config.AddTarget(new TargetRecord { Repo = name, Intake = IntakeRules.Label, Enabled = true });
                runner.Wake();
                return Results.Json(new EnrollResult(true, null, $"{name} enrolled — first scan running…"));
            }
            catch (Exception ex)
            {
                return Results.Json(new EnrollResult(false, ex.Message, null), statusCode: 500);
            }
        });

        api.MapPut("/targets", (TargetRecord target, ConfigStore config, FleetRunner runner) =>
        {
            if (string.IsNullOrWhiteSpace(target.DefinitionOfDone))
            {
                return Results.Json(new CommandResult(false, "Definition of done must be non-empty."), statusCode: 400);
            }
            if (target.Intake is not (IntakeRules.Label or IntakeRules.All))
            {
                return Results.Json(new CommandResult(false, $"unknown intake rule '{target.Intake}'."), statusCode: 400);
            }
            try
            {
                config.UpdateTarget(target);
                runner.Wake();
                return Results.Json(new CommandResult(true, null));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new CommandResult(false, ex.Message), statusCode: 400);
            }
        });

        api.MapDelete("/targets", (string repo, ConfigStore config, FleetRunner runner) =>
        {
            config.RemoveTargets([repo]);
            runner.Wake();
            return Results.Json(new CommandResult(true, null));
        });

        api.MapPut("/orgs", (OrgRecord org, ConfigStore config, FleetRunner runner) =>
        {
            if (string.IsNullOrWhiteSpace(org.DefinitionOfDone))
            {
                return Results.Json(new CommandResult(false, "Definition of done must be non-empty."), statusCode: 400);
            }
            if (org.Intake is not (IntakeRules.Label or IntakeRules.All))
            {
                return Results.Json(new CommandResult(false, $"unknown intake rule '{org.Intake}'."), statusCode: 400);
            }
            try
            {
                config.UpdateOrg(org);
                runner.Wake();
                return Results.Json(new CommandResult(true, null));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new CommandResult(false, ex.Message), statusCode: 400);
            }
        });

        api.MapDelete("/orgs", (string org, ConfigStore config, FleetRunner runner) =>
        {
            config.RemoveOrg(org);
            runner.Wake();
            return Results.Json(new CommandResult(true, null));
        });
    }

    private static PipelineCatalogView PipelineCatalog(ConfigStore config) => new(
        config.Pipelines(),
        config.CurrentDefaultPipelineId(),
        config.Profiles().Select(FleetViews.Profile).ToList(),
        SkillLibrary.NamesOnDisk(),
        PipelineRules.PlaceholderNames);

    private static ProfilesView ProfileCatalog(ConfigStore config) => new(
        config.Profiles().Select(FleetViews.ProfileForPage).ToList(),
        FleetViews.Binding(config.Utility()),
        FleetViews.Binding(config.Fallback()),
        Enum.GetValues<AgentFlavour>().Select(flavour => new FlavourView(
            flavour.ToString(),
            AgentFlavours.Label(flavour),
            AgentFlavours.Executable(flavour),
            FlavourFacts.Engine(flavour),
            FlavourFacts.Fact(flavour),
            FlavourFacts.KeyMode(flavour),
            FlavourFacts.CatalogNote(flavour),
            FlavourFacts.KeylessNote(flavour),
            FlavourFacts.StaticCatalog(flavour),
            FlavourFacts.ExternalCatalogUrl(flavour))).ToList(),
        Referrers(config));

    /// <summary>What pins each profile, in the words the editor shows.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Referrers(ConfigStore config)
    {
        var profiles = config.Profiles();
        var pipelines = config.Pipelines();
        var utility = config.Utility();
        var fallback = config.Fallback();
        return profiles.ToDictionary(
            profile => profile.Id,
            profile =>
            {
                var referrers = pipelines
                    .SelectMany(pipeline => pipeline.Phases
                        .Where(phase => phase.ProfileId == profile.Id)
                        .Select(phase => $"{pipeline.Name} › {phase.Name}"))
                    .ToList();
                if (utility?.ProfileId == profile.Id)
                {
                    referrers.Add("utility binding (triage/planning)");
                }
                if (fallback?.ProfileId == profile.Id)
                {
                    referrers.Add("fallback binding (pin fallback / unpinned default)");
                }
                return (IReadOnlyList<string>)referrers;
            });
    }

    private static IResult SetBinding(BindingRequest request, bool fallback, ConfigStore config)
    {
        try
        {
            UtilityBinding? binding = string.IsNullOrWhiteSpace(request.ProfileId)
                ? null
                : new UtilityBinding
                {
                    ProfileId = request.ProfileId,
                    ModelId = string.IsNullOrWhiteSpace(request.ModelId) ? null : request.ModelId,
                };
            if (fallback)
            {
                config.SetFallback(binding);
            }
            else
            {
                config.SetUtility(binding);
            }
            return Results.Json(new CommandResult(true, null));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new CommandResult(false, ex.Message), statusCode: 400);
        }
    }

    private static async Task<IResult> ProbeAsync(
        ProbeRequest request,
        ConfigStore config,
        OpenCodeEngine engine,
        CancellationToken ct)
    {
        try
        {
            if (!Enum.TryParse<AgentFlavour>(request.Flavour, ignoreCase: true, out var flavour))
            {
                return Results.Json(new ProbeView(false, $"unknown flavour '{request.Flavour}'", false));
            }

            var existing = request.Id is { Length: > 0 } id ? config.ProfileFor(id) : null;
            var label = AgentFlavours.Label(flavour);
            var providerId = AgentFlavours.OpencodeProvider(flavour);

            IReadOnlyList<OpenCodeEngine.ProviderCatalog>? catalog = null;
            try
            {
                catalog = await engine.GetConfigProvidersAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
            {
                catalog = null;
            }

            var probeKey = ProfileCredential.Resolve(
                request.ApiKey is { Length: > 0 } pasted ? pasted : existing?.ApiKey);

            var keyOk = true;
            var keyRejected = false;
            string keyNote;
            if (probeKey.Length > 0 && FlavourFacts.ExternalCatalogUrl(flavour) is not null)
            {
                try
                {
                    var test = await FlavourFacts.ProbeKeyAsync(flavour, probeKey);
                    keyOk = !test.Definitive || test.Accepted;
                    keyRejected = test.Definitive && !test.Accepted;
                    keyNote = $"{test.Detail} — injected per-run at run start";
                }
                catch (Exception ex)
                {
                    keyNote = $"no verdict — key test failed ({ex.GetType().Name})";
                }
            }
            else if (probeKey.Length > 0)
            {
                keyNote = "key set — injected per-run at run start (no key-test endpoint for this flavour)";
            }
            else
            {
                keyNote = $"no key — native login would win · keyless catalog: {FlavourFacts.KeylessNote(flavour)}";
            }

            // Fall back to the public Go models list when the serve snapshot has no provider
            // (boot-time snapshot predates the credential). The external list never throws.
            var external = await FlavourFacts.FetchExternalModelsAsync(flavour, ct);
            var provider = catalog?.FirstOrDefault(p => p.Id == providerId);
            var liveModels = provider?.Models ?? [];
            var effectiveModels = liveModels.Count > 0 ? liveModels : external;
            var effectiveProvider = provider is not null ? provider with { Models = effectiveModels } : new OpenCodeEngine.ProviderCatalog(providerId ?? "", "OpenCode Go", effectiveModels);

            if (catalog is null && external.Count == 0)
            {
                return Results.Json(new ProbeView(
                    false,
                    "the fleet serve is unreachable and the Go models endpoint is unreachable — no live catalog. Restart the fleet app if this persists.",
                    keyRejected));
            }

            if (provider is null && external.Count > 0)
            {
                var models = request.Models ?? existing?.Models ?? [];
                var scopedExternal = effectiveModels.Select(m => $"{providerId}/{m}").ToList();
                var outsideExt = models.Where(m => !scopedExternal.Contains(m, StringComparer.Ordinal)).ToList();
                var msg = $"{providerId}: serve snapshot has no provider yet — {keyNote}. " +
                    $"Public models list shows {external.Count} models (e.g. {scopedExternal.Take(2).Aggregate((a, b) => a + ", " + b)}). " +
                    $"Palette check vs public list: {models.Count - outsideExt.Count}/{models.Count} in list" +
                    (outsideExt.Count > 0 ? $" — {outsideExt.Count} pick(s) not in the public list ({string.Join(", ", outsideExt.Take(3))})." : ". Save the key and restart the serve to bake it into the snapshot.");
                return Results.Json(new ProbeView(false, msg, keyRejected));
            }

            var scopedCatalog = FlavourFacts.Catalog(flavour, catalog);
            var models2 = request.Models ?? existing?.Models ?? [];
            var outside = models2.Where(m => !scopedCatalog.Contains(m, StringComparer.Ordinal)).ToList();
            var providerCount = effectiveProvider.Models.Count;
            var message =
                $"{providerId}: catalog reachable — {providerCount} models {(provider is null ? "(public list)" : "in the boot-time snapshot")} · " +
                $"{keyNote} · palette filters to your picks ({models2.Count} selected)" +
                (outside.Count > 0
                    ? $". {outside.Count} palette pick(s) aren't in this snapshot " +
                      $"({string.Join(", ", outside.Take(3))}{(outside.Count > 3 ? ", …" : string.Empty)}) — " +
                      "stale snapshot? Restart the serve after rotating keys."
                    : ".");

            return Results.Json(new ProbeView(outside.Count == 0 && keyOk, message, keyRejected));
        }
        catch (Exception ex)
        {
            return Results.Json(new ProbeView(false, $"probe failed ({ex.GetType().Name}): {ex.Message}", false));
        }
    }

    private static async Task<IResult> VerifyProfileAsync(
        ProfileVerifyRequest request,
        ConfigStore config,
        OpenCodeEngine engine,
        FleetOptions options,
        CancellationToken ct)
    {
        if (!Enum.TryParse<AgentFlavour>(request.Flavour, ignoreCase: true, out var flavour))
        {
            return Results.Json(new ProfileVerifyView(false, $"unknown flavour '{request.Flavour}'", null, null, null, 0, 0, 0, false));
        }
        if (!AgentFlavours.Executable(flavour))
        {
            return Results.Json(new ProfileVerifyView(false, $"{AgentFlavours.Label(flavour)}: no execution adapter yet — cannot make a live model call.", null, null, null, 0, 0, 0, false));
        }
        var existing = request.Id is { Length: > 0 } id ? config.ProfileFor(id) : null;
        var credential = ProfileCredential.Resolve(request.ApiKey is { Length: > 0 } pasted ? pasted : existing?.ApiKey);
        string? modelId = !string.IsNullOrWhiteSpace(request.ModelId) ? request.ModelId
            : (request.Models?.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))
               ?? existing?.Models.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)));
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return Results.Json(new ProfileVerifyView(false, "no model to verify — pick a palette model first.", null, null, null, 0, 0, 0, false));
        }
        var providerId = AgentFlavours.OpencodeProvider(flavour)!;
        // Inject this credential (draft or stored) — boot-time auth means a changed key costs one serve restart, which is exactly the live proof.
        try
        {
            if (credential.Length > 0)
            {
                await engine.EnsureCredentialAsync(providerId, credential, ct);
            }
            else if (!engine.IsRunning)
            {
                await engine.StartAsync(ct);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or OperationCanceledException)
        {
            return Results.Json(new ProfileVerifyView(false, $"serve could not bake the key: {ex.Message[..Math.Min(300, ex.Message.Length)]}", null, modelId, providerId, 0, 0, 0, false));
        }
        var probeDir = Path.Combine(options.DataDir, "probe");
        Directory.CreateDirectory(probeDir);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.ProbeTimeoutSeconds));
        string? session = null;
        try
        {
            session = await engine.CreateSessionAsync(probeDir, "agent-factory key verify (hi)", modelId, deadline.Token);
            var result = await engine.RunPromptAsync(session, probeDir, "Say hi in one short sentence. Who are you?", deadline.Token);
            var text = (result.Text ?? "").Trim();
            var shortText = text.Length > 500 ? text[..500] + "…" : text;
            var ok = result.TokensOut > 0 && text.Length > 0;
            var msg = ok
                ? $"{modelId}: live model call ok ({result.TokensIn}in/{result.TokensOut}out, cost {result.Cost}) — model replied: “{shortText}”"
                : $"{modelId}: empty reply with zero output tokens — the silent-auth shape (key rejected or model dead).";
            return Results.Json(new ProfileVerifyView(ok, msg, string.IsNullOrWhiteSpace(shortText) ? null : shortText, modelId, providerId, result.TokensIn, result.TokensOut, result.Cost, !ok));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Results.Json(new ProfileVerifyView(false, $"{modelId}: verify timed out after {options.ProbeTimeoutSeconds}s and was aborted.", null, modelId, providerId, 0, 0, 0, false));
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException or TimeoutException)
        {
            var detail = ex.Message.Length > 400 ? ex.Message[..400] : ex.Message;
            var rejected = detail.Contains("401") || detail.Contains("Invalid credential", StringComparison.OrdinalIgnoreCase);
            return Results.Json(new ProfileVerifyView(false, $"{modelId}: {ex.GetType().Name}: {detail}", null, modelId, providerId, 0, 0, 0, rejected));
        }
        finally
        {
            if (session is not null)
            {
                try { await engine.AbortAsync(session, probeDir, CancellationToken.None); } catch { }
            }
        }
    }

    private static async Task<IResult> VerifyGithubAsync(
        GithubVerifyRequest request,
        ConfigStore config,
        FleetOptions options,
        CancellationToken ct)
    {
        var draft = string.IsNullOrWhiteSpace(request.Token) ? null : request.Token.Trim();
        var token = !string.IsNullOrWhiteSpace(draft) ? draft : config.EffectiveGithubToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return Results.Json(new GithubVerifyView(false, "no token — paste GH_TOKEN above Verify or save it via the UI.", null, null, null, null, [], []));
        }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentFactory/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());

        async Task<(System.Net.HttpStatusCode Status, string Body, System.Collections.Generic.Dictionary<string, string> Headers)> GetAsync(string path)
        {
            var headers = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com{path}");
                using var res = await http.SendAsync(req, ct);
                foreach (var kv in res.Headers) headers[kv.Key] = string.Join(",", kv.Value);
                var body = await res.Content.ReadAsStringAsync(ct);
                return (res.StatusCode, body, headers);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return ((System.Net.HttpStatusCode)0, ex.Message, headers);
            }
        }

        var (userStatus, userBody, userHeaders) = await GetAsync("/user");
        string? userLogin = null;
        if (userStatus == System.Net.HttpStatusCode.OK)
        {
            try { using var doc = System.Text.Json.JsonDocument.Parse(userBody); userLogin = doc.RootElement.TryGetProperty("login", out var l) ? l.GetString() : null; } catch { }
        }
        string? scopes = null;
        if (userHeaders.TryGetValue("X-OAuth-Scopes", out var xo) && !string.IsNullOrWhiteSpace(xo)) scopes = xo;
        else if (userHeaders.TryGetValue("X-Accepted-OAuth-Scopes", out var xa) && !string.IsNullOrWhiteSpace(xa)) scopes = xa;

        int? rateRemaining = null; int? rateLimit = null;
        var (rateStatus, rateBody, _) = await GetAsync("/rate_limit");
        if (rateStatus == System.Net.HttpStatusCode.OK)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(rateBody);
                if (doc.RootElement.TryGetProperty("resources", out var res) && res.TryGetProperty("core", out var core))
                {
                    if (core.TryGetProperty("remaining", out var rem)) rateRemaining = rem.GetInt32();
                    if (core.TryGetProperty("limit", out var lim)) rateLimit = lim.GetInt32();
                }
            }
            catch { }
        }

        var repos = new List<GhRepoCheckView>();
        var distinctRepos = config.Targets().Select(t => t.Repo).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r).ToList();
        foreach (var repo in distinctRepos)
        {
            var (st, body, _) = await GetAsync($"/repos/{repo}");
            if (st != System.Net.HttpStatusCode.OK)
            {
                var msg = st == System.Net.HttpStatusCode.NotFound ? "404 not found / no access" : $"HTTP {(int)st} {Truncate(body, 120)}";
                repos.Add(new GhRepoCheckView(repo, false, msg, null, null, null));
                continue;
            }
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;
                var def = root.TryGetProperty("default_branch", out var db) ? db.GetString() : null;
                bool? push = null; bool? pull = null;
                if (root.TryGetProperty("permissions", out var perm))
                {
                    if (perm.TryGetProperty("push", out var pu)) push = pu.GetBoolean();
                    if (perm.TryGetProperty("pull", out var pl)) pull = pl.GetBoolean();
                }
                // Write-access probe: label read (fleet needs to ensure label + list issues + push).
                var (labelSt, _, _) = await GetAsync($"/repos/{repo}/labels/{options.OptInLabel}");
                var labelNote = labelSt == System.Net.HttpStatusCode.OK ? "label ok" : (labelSt == System.Net.HttpStatusCode.NotFound ? "label missing (fleet will create)" : $"label HTTP {(int)labelSt}");
                var ok = (push ?? false) && (pull ?? false);
                var msg = $"accessible • push:{push?.ToString().ToLower() ?? "?"} pull:{pull?.ToString().ToLower() ?? "?"} • {labelNote}";
                if (def is not null) msg += $" • default:{def}";
                if (!ok) msg += " • NEEDS push access to open PRs";
                repos.Add(new GhRepoCheckView(repo, ok, msg, push, pull, def));
            }
            catch
            {
                repos.Add(new GhRepoCheckView(repo, false, "repo JSON parse failed", null, null, null));
            }
            if (ct.IsCancellationRequested) break;
        }

        var orgChecks = new List<GhOrgCheckView>();
        foreach (var org in config.Orgs().Select(o => o.Org).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(o => o))
        {
            var (st, _, _) = await GetAsync($"/orgs/{org}");
            var (memSt, memBody, _) = await GetAsync($"/user/memberships/orgs/{org}");
            string? role = null;
            if (memSt == System.Net.HttpStatusCode.OK)
            {
                try { using var doc = System.Text.Json.JsonDocument.Parse(memBody); role = doc.RootElement.TryGetProperty("role", out var r) ? r.GetString() : null; } catch { }
            }
            var ok = st == System.Net.HttpStatusCode.OK;
            var msg = ok
                ? (memSt == System.Net.HttpStatusCode.OK ? $"org reachable • membership: {role ?? "member"}" : "org reachable • not a member (private repos hidden)")
                : (st == System.Net.HttpStatusCode.NotFound ? "404 org not found / no access" : $"HTTP {(int)st}");
            orgChecks.Add(new GhOrgCheckView(org, ok, msg, role));
        }

        var allReposOk = repos.Count > 0 && repos.All(r => r.Ok);
        var userOk = userStatus == System.Net.HttpStatusCode.OK && userLogin is not null;
        var overall = userOk && allReposOk;
        string message;
        if (!userOk)
        {
            message = userStatus == (System.Net.HttpStatusCode)401
                ? "GitHub rejected the token (401 Bad credentials) — no connectivity to api.github.com."
                : $"GitHub /user failed HTTP {(int)userStatus} {Truncate(userBody, 120)}";
        }
        else if (overall)
        {
            message = $"@ {userLogin} • {repos.Count}/{repos.Count} repos push-accessible • {orgChecks.Count} org(s) • rate {rateRemaining}/{rateLimit}" + (string.IsNullOrWhiteSpace(scopes) ? "" : $" • scopes: {scopes}");
        }
        else
        {
            var failed = repos.Where(r => !r.Ok).Select(r => r.Repo).ToList();
            var skipped = failed.Count == 0 ? "" : $" • failed: {string.Join(", ", failed.Take(5))}{(failed.Count > 5 ? "…" : "")}";
            message = $"@ {userLogin} • only {repos.Count(r => r.Ok)}/{repos.Count} repos push-accessible{skipped} • rate {rateRemaining}/{rateLimit}" + (string.IsNullOrWhiteSpace(scopes) ? "" : $" • scopes: {scopes}");
        }
        return Results.Json(new GithubVerifyView(overall, message, userLogin, scopes, rateRemaining, rateLimit, repos, orgChecks));

        static string Truncate(string s, int max) => string.IsNullOrWhiteSpace(s) ? "" : (s.Length <= max ? s.Trim().Replace("\n", " ") : s.Trim().Replace("\n", " ")[..max] + "…");
    }

    private static OrgScanView ToScanView(OrgScanner.OrgScanView scan) =>
        new(scan.ListedAt, scan.Repos);
}
