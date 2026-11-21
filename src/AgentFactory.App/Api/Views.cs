using AgentFactory.App.Fleet;

namespace AgentFactory.App.Api;

/// <summary>
/// The JSON the Agent Factory app renders. Every record here is a reading of
/// the fleet's own state: the API adds no policy, moves no run and writes
/// nothing except through the explicit commands on the other side of the
/// endpoint table.
/// </summary>
public sealed record EngineView(bool Up, int Port);

public sealed record RunCountsView(
    int Total,
    int Succeeded,
    int Failed,
    int Working,
    int Queued,
    int Cancelled,
    int Disarmed,
    int Routed);

public sealed record FleetView(
    string App,
    string Tagline,
    EngineView Engine,
    int PollSeconds,
    int TargetCount,
    string OptInLabel,
    bool Paused,
    RunCountsView Runs,
    DateTimeOffset Now);

public sealed record RunSummaryView(
    string RunId,
    string Repo,
    int IssueNumber,
    string IssueTitle,
    string IssueUrl,
    string State,
    string Kind,
    int Attempt,
    string? Branch,
    string? PrUrl,
    string? PipelineName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error,
    string? ParentRunId,
    int ChildOrder,
    bool Live,
    string? TriageVerdict,
    string? TriageSummary,
    string? RoutedIssueUrl);

public sealed record PhaseView(
    string Name,
    IReadOnlyList<string> Skills,
    string? SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    decimal Cost,
    long TokensIn,
    long TokensOut,
    string? Summary,
    string? ProfileName,
    string? Flavour,
    string? ModelId,
    string? ProviderId);

public sealed record SessionView(
    string Id,
    string Title,
    string Directory,
    string Agent,
    string ModelId,
    decimal Cost,
    long TokensIn,
    long TokensOut,
    DateTimeOffset Updated);

public sealed record TranscriptLineView(DateTimeOffset Time, string Role, string Text);

public sealed record TranscriptView(
    int SelectedPhaseIndex,
    string? SessionId,
    SessionView? Session,
    IReadOnlyList<TranscriptLineView> Transcript);

public sealed record RunDetailView(
    RunSummaryView Run,
    string IssueBody,
    string? AgentSummary,
    IReadOnlyList<PhaseView> Phases,
    string? LogTail,
    IReadOnlyList<RunSummaryView> Children,
    RunSummaryView? Parent,
    SessionView? Session,
    IReadOnlyList<TranscriptLineView> Transcript,
    int SelectedPhaseIndex,
    string? SelectedSessionId);

public sealed record ProfileSummaryView(
    string Id,
    string Name,
    string Flavour,
    string FlavourLabel,
    IReadOnlyList<string> Models,
    bool HasKey);

public sealed record PipelineCatalogView(
    IReadOnlyList<PipelineDefinition> Pipelines,
    string DefaultPipelineId,
    IReadOnlyList<ProfileSummaryView> Profiles,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Placeholders);

public sealed record SavePipelineResult(bool Ok, IReadOnlyList<string> Errors, string? Id);

public sealed record FlavourView(
    string Value,
    string Label,
    bool Executable,
    string Engine,
    string Fact,
    string KeyMode,
    string CatalogNote,
    string KeylessNote,
    IReadOnlyList<string> StaticCatalog,
    string? ExternalCatalogUrl);

public sealed record UtilityBindingView(string? ProfileId, string? ModelId);

public sealed record ProfileView(
    string Id,
    string Name,
    string Flavour,
    string FlavourLabel,
    IReadOnlyList<string> Models,
    bool HasKey);

public sealed record ProfilesView(
    IReadOnlyList<ProfileView> Profiles,
    UtilityBindingView? Utility,
    UtilityBindingView? Fallback,
    IReadOnlyList<FlavourView> Flavours,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Referrers);

public sealed record ProviderView(string Id, string Name, IReadOnlyList<string> Models);

public sealed record CatalogView(bool Available, IReadOnlyList<ProviderView> Providers);

public sealed record ProbeView(bool Ok, string Message, bool KeyRejected);

public sealed record GithubTokenView(bool HasStoredToken, bool HasEffectiveToken, string Source, string? TokenPreview);
public sealed record GithubTokenSaveRequest(string? Token);
public sealed record GithubVerifyRequest(string? Token);
public sealed record GhRepoCheckView(string Repo, bool Ok, string Message, bool? Push, bool? Pull, string? DefaultBranch);
public sealed record GhOrgCheckView(string Org, bool Ok, string Message, string? Role);
public sealed record GithubVerifyView(bool Ok, string Message, string? UserLogin, string? Scopes, int? RateLimitRemaining, int? RateLimitLimit, IReadOnlyList<GhRepoCheckView> Repos, IReadOnlyList<GhOrgCheckView> Orgs);

public sealed record ProfileVerifyRequest(string? Id, string Flavour, string? ApiKey, string? ModelId, IReadOnlyList<string>? Models);
public sealed record ProfileVerifyView(bool Ok, string Message, string? ModelReply, string? ModelId, string? ProviderId, long TokensIn, long TokensOut, decimal Cost, bool KeyRejected);

public sealed record TargetPollView(DateTimeOffset At, string? Note);

public sealed record PipelineChoiceView(string Id, string Name);

public sealed record OrgScanView(
    DateTimeOffset? ListedAt,
    IReadOnlyList<OrgRepoSnapshot> Repos);

public sealed record TargetsView(
    IReadOnlyList<TargetRecord> Targets,
    IReadOnlyList<OrgRecord> Orgs,
    IReadOnlyList<PipelineChoiceView> Pipelines,
    string DefaultPipelineId,
    IReadOnlyDictionary<string, TargetPollView> LastPoll,
    IReadOnlyDictionary<string, int> Queued,
    IReadOnlyDictionary<string, OrgScanView> OrgScans);

public sealed record EnrollResult(bool Ok, string? Error, string? Note);

public sealed record CommandResult(bool Ok, string? Error);

public sealed record PauseRequest(bool Paused);

public sealed record PauseResult(bool Paused);

public sealed record EnrollRequest(string Kind, string Name);

public sealed record ProfileSaveRequest(
    string? Id,
    string Name,
    string Flavour,
    string? ApiKey,
    bool ClearKey,
    IReadOnlyList<string>? Models);

public sealed record ProfileSaveResult(bool Ok, IReadOnlyList<string> Errors, ProfileView? Profile);

public sealed record BindingRequest(string? ProfileId, string? ModelId);

public sealed record ProbeRequest(
    string? Id,
    string Flavour,
    string? ApiKey,
    IReadOnlyList<string>? Models);

public static class FleetViews
{
    public static RunSummaryView Summary(RunRecord run) => new(
        run.RunId,
        run.Repo,
        run.IssueNumber,
        run.IssueTitle,
        run.IssueUrl,
        Slug(run.State),
        Slug(run.Kind),
        run.Attempt,
        run.Branch,
        run.PrUrl,
        run.PipelineName,
        run.CreatedAt,
        run.StartedAt,
        run.FinishedAt,
        run.Error,
        run.ParentRunId,
        run.ChildOrder,
        run.State is RunState.Queued or RunState.Working,
        run.TriageVerdict,
        run.TriageSummary,
        run.RoutedIssueUrl);

    public static PhaseView Phase(PhaseRecord phase) => new(
        phase.Name,
        phase.Skills,
        phase.SessionId,
        phase.StartedAt,
        phase.FinishedAt,
        phase.Cost,
        phase.TokensIn,
        phase.TokensOut,
        phase.Summary,
        phase.ProfileName,
        phase.Flavour,
        phase.ModelId,
        phase.ProviderId);

    public static SessionView? Session(OpenCodeEngine.SessionSummary? session) => session is null
        ? null
        : new SessionView(
            session.Id,
            session.Title,
            session.Directory,
            session.Agent,
            session.ModelId,
            session.Cost,
            session.TokensIn,
            session.TokensOut,
            session.Updated);

    public static ProfileSummaryView Profile(AgentProfile profile) => new(
        profile.Id,
        profile.Name,
        profile.Flavour.ToString(),
        AgentFlavours.Label(profile.Flavour),
        profile.Models,
        profile.ApiKey.Length > 0);

    public static ProfileView ProfileForPage(AgentProfile profile) => new(
        profile.Id,
        profile.Name,
        profile.Flavour.ToString(),
        AgentFlavours.Label(profile.Flavour),
        profile.Models,
        profile.ApiKey.Length > 0);

    public static UtilityBindingView? Binding(UtilityBinding? binding) =>
        binding is null ? null : new UtilityBindingView(binding.ProfileId, binding.ModelId);

    public static RunCountsView Counts(IReadOnlyList<RunRecord> runs) => new(
        runs.Count,
        runs.Count(r => r.State == RunState.Succeeded),
        runs.Count(r => r.State == RunState.Failed),
        runs.Count(r => r.State == RunState.Working),
        runs.Count(r => r.State == RunState.Queued),
        runs.Count(r => r.State == RunState.Cancelled),
        runs.Count(r => r.State == RunState.Disarmed),
        runs.Count(r => r.State == RunState.Routed));

    private static string Slug(RunState state) => state switch
    {
        RunState.Queued => "queued",
        RunState.Working => "working",
        RunState.Succeeded => "succeeded",
        RunState.Failed => "failed",
        RunState.Cancelled => "cancelled",
        RunState.Disarmed => "disarmed",
        RunState.Routed => "routed",
        _ => state.ToString().ToLowerInvariant(),
    };

    private static string Slug(RunKind kind) => kind switch
    {
        RunKind.Normal => "normal",
        RunKind.Parent => "parent",
        RunKind.Child => "child",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
