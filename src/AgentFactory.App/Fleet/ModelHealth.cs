using System.Collections.Concurrent;
using System.Text.Json;

namespace AgentFactory.App.Fleet;

/// <summary>One phase's model decision, made once per attempt before any
/// session exists (ticket 044): the pin when it probes healthy, the fleet
/// fallback binding when a pin is dead or the phase is unpinned, engine
/// defaults when nothing else is configured. <see cref="Note"/> carries the
/// run-log wording for the phase (including the ordinary pinned wording);
/// <see cref="FailReason"/> non-null fails the attempt fast — before any
/// long session is bet on a model the probe already saw fail.</summary>
public sealed record ModelPlan(AgentProfile? Profile, string? ModelId, string Note, string? FailReason)
{
    /// <summary>Engine defaults, byte-for-byte the pre-fallback behavior.</summary>
    public static ModelPlan Default() => new(null, null, "", null);
}

/// <summary>The planner half of model health (ticket 044): pure — the probe
/// verdicts are taken beforehand and passed in, so the whole decision table
/// is unit-testable without a serve.</summary>
public static class ModelPlanner
{
    /// <summary>Resolve what a phase actually runs. <paramref name="pin"/> is
    /// the phase's pinned profile (null = unpinned; pins are atomic, so a pin
    /// always carries its model — run-start validation enforces that before
    /// planning); <paramref name="fallback"/> is the run's snapshot of the
    /// fleet fallback binding (null = none configured, or it was unusable);
    /// <paramref name="health"/> maps every model id this attempt may run to
    /// its probe verdict.</summary>
    public static ModelPlan Plan(
        AgentProfile? pin, string? pinModel,
        (AgentProfile Profile, string ModelId)? fallback,
        IReadOnlyDictionary<string, ModelHealthProbe.Verdict> health)
    {
        if (pin is not null)
        {
            var verdict = health[pinModel!];
            if (verdict.Healthy)
                return new ModelPlan(pin, pinModel,
                    $"pinned {AgentFlavours.Label(pin.Flavour)} '{pin.Name}' model {pinModel}", null);

            if (fallback is not { } fb)
                return new ModelPlan(null, null, "",
                    $"pinned model '{pinModel}' failed its health probe ({verdict.Detail}) — " +
                    "no fallback binding is configured");

            var fbVerdict = health[fb.ModelId];
            if (!fbVerdict.Healthy)
                return new ModelPlan(null, null, "",
                    $"pinned model '{pinModel}' failed its health probe ({verdict.Detail}) — " +
                    $"and the fallback model '{fb.ModelId}' failed its own probe ({fbVerdict.Detail})");

            return new ModelPlan(fb.Profile, fb.ModelId,
                $"PIN FALLBACK: pinned model '{pinModel}' failed its health probe ({verdict.Detail}) — " +
                $"running on fallback '{fb.ModelId}'", null);
        }

        if (fallback is not { } unpinnedFb) return ModelPlan.Default();

        var unpinnedVerdict = health[unpinnedFb.ModelId];
        if (!unpinnedVerdict.Healthy)
            return new ModelPlan(null, null, "",
                $"fleet fallback model '{unpinnedFb.ModelId}' failed its health probe " +
                $"({unpinnedVerdict.Detail}) — unset or repair the fallback binding");

        return new ModelPlan(unpinnedFb.Profile, unpinnedFb.ModelId,
            $"unpinned — fleet fallback '{unpinnedFb.ModelId}' (deterministic default)", null);
    }

    /// <summary>The run-start read of the fleet fallback binding (ticket 044):
    /// a usable (profile, model) pair, or a null profile plus the reason it
    /// isn't usable for the run log. A null reason with a null profile means
    /// unset — silence is correct there. The fail-soft
    /// <see cref="UsableFallback"/> and the executor's logged snapshot both
    /// read this one shape.</summary>
    public static (AgentProfile? Profile, string ModelId, string? UnusableReason) FallbackSnapshot(
        ConfigStore config)
    {
        if (config.Fallback() is not { } binding) return (null, "", null);
        if (binding.ProfileId is not { Length: > 0 } id || binding.ModelId is not { Length: > 0 } model)
            return (null, "", "is unusable (profile and model together, or neither)");
        var profile = config.ProfileFor(id);
        if (profile is null)
            return (null, "", $"names profile '{id}' which no longer exists");
        if (!profile.Models.Contains(model))
            return (null, "", $"names '{model}' which is not in profile '{profile.Name}'s palette");
        if (!AgentFlavours.Executable(profile.Flavour))
            return (null, "", $"names flavour {AgentFlavours.Label(profile.Flavour)} which has no execution adapter");
        return (profile, model, null);
    }

    /// <summary>The usable fleet fallback binding, or null — the fail-soft
    /// read the triage and planning sessions use, over
    /// <see cref="FallbackSnapshot"/> (which logs its reasons to the run log
    /// in the executor instead).</summary>
    public static (AgentProfile Profile, string ModelId)? UsableFallback(ConfigStore config) =>
        FallbackSnapshot(config) is { Profile: { } profile, ModelId: { Length: > 0 } model }
            ? (profile, model)
            : null;
}

/// <summary>Model health (ticket 044): a tiny bounded session per model —
/// "Reply with exactly: ok" — through the same serve real sessions ride, so
/// the probe exercises the exact path a phase would. The verdict reads the
/// signature 042 taught the fleet to trust: a non-empty reply with output
/// tokens is alive; empty-with-zero-tokens (silent auth failure) and
/// timeouts are dead. Results cache per model id for a TTL window — one
/// probe per model per window no matter how many phases or runs ask — and
/// probes cost real tokens, deliberately: the only faithful upstream check
/// is a real completion (catalog presence says nothing about a model being
/// alive — 041's free model was listed and hung).</summary>
public sealed class ModelHealthProbe(
    OpenCodeEngine engine,
    FleetOptions options,
    ILogger<ModelHealthProbe> logger)
{
    public sealed record Verdict(bool Healthy, string Detail);

    private sealed record CacheEntry(Verdict Verdict, DateTimeOffset At);

    /// <summary>What a utility session should run after the health ladder:
    /// the model (null = engine defaults), the profile whose flavour the
    /// session's engine must be switched to (null = leave it as the caller
    /// set it, or reset it to defaults when the model is null), and a note
    /// for the wording when anything other than "healthy, as pinned"
    /// happened.</summary>
    public sealed record UtilityShore(string? ModelId, AgentProfile? Profile, string Note);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    /// <summary>Probe verdicts stay believable for this long; a run's phases
    /// and its retry share one verdict per model inside the window.</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private string ProbeDir => Path.Combine(options.DataDir, "probe");

    public async Task<Verdict> ProbeAsync(string modelId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(modelId, out var hit) && DateTimeOffset.UtcNow - hit.At < Ttl)
        {
            return hit.Verdict;
        }

        logger.LogInformation("model-health probe: {Model} (no fresh verdict cached)", modelId);
        var verdict = await ProbeOnceAsync(modelId, ct);
        _cache[modelId] = new CacheEntry(verdict, DateTimeOffset.UtcNow);
        logger.LogInformation("model-health probe: {Model} -> {Verdict} ({Detail})",
            modelId, verdict.Healthy ? "ALIVE" : "DEAD", verdict.Detail);
        return verdict;
    }

    /// <summary>The utility model's health ladder (ticket 044), shared by the
    /// triage and parent-planning sessions: probe the pinned utility model —
    /// a dead upstream otherwise hangs the session into the run-level
    /// timeout, an orchestration error with no retry (041's twin failures) —
    /// and re-shore a dead one onto the fleet fallback binding (credential
    /// ensured, probe checked) or engine defaults. Never fails: the utility
    /// session is best-effort and the run must not die over a model here.
    /// Pass a null model (engine-defaults binding) and nothing is probed.</summary>
    public async Task<UtilityShore> ResolveUtilityModelAsync(
        ConfigStore config, string? utilityModel, CancellationToken ct = default)
    {
        if (utilityModel is not { Length: > 0 } probed)
            return new UtilityShore(utilityModel, null, "");

        var verdict = await ProbeAsync(probed, ct);
        if (verdict.Healthy) return new UtilityShore(probed, null, "");

        var note = $"utility model probe failed ({verdict.Detail})";
        if (ModelPlanner.UsableFallback(config) is not { } shore)
            return new UtilityShore(null, null, note + " — no usable fallback binding, engine default used");

        // The fallback's credential may ride a provider the utility pin
        // didn't touch — ensure it first (PUT-only in steady state; a first
        // bake restart is safe here: no session of this run exists yet).
        if (AgentFlavours.OpencodeProvider(shore.Profile.Flavour) is { } shoreProvider
            && ProfileCredential.Resolve(shore.Profile.ApiKey) is { Length: > 0 } shoreCredential)
        {
            await engine.EnsureCredentialAsync(shoreProvider, shoreCredential, ct);
        }
        var shoreVerdict = await ProbeAsync(shore.ModelId, ct);
        if (!shoreVerdict.Healthy)
            return new UtilityShore(null, null,
                $"{note} — fallback probe failed too ({shoreVerdict.Detail}), engine default used");
        return new UtilityShore(shore.ModelId, shore.Profile, $"{note} — fallback '{shore.ModelId}' used");
    }

    private async Task<Verdict> ProbeOnceAsync(string modelId, CancellationToken ct)
    {
        Directory.CreateDirectory(ProbeDir);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.ProbeTimeoutSeconds));
        string? session = null;
        try
        {
            session = await engine.CreateSessionAsync(
                ProbeDir, "agent-factory model-health probe", modelId, deadline.Token);
            var result = await engine.RunPromptAsync(
                session, ProbeDir, "Reply with exactly: ok", deadline.Token);
            return result.TokensOut > 0 && result.Text.Length > 0
                ? new Verdict(true, $"probe ok ({result.TokensIn}in/{result.TokensOut}out, cost {result.Cost})")
                : new Verdict(false,
                    "empty reply with zero output tokens — the silent-auth shape (ticket 042)");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Verdict(false,
                $"probe exceeded {options.ProbeTimeoutSeconds}s and was aborted (the 041 free-model hang shape)");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            return new Verdict(false, $"{ex.GetType().Name}: {ex.Message[..Math.Min(300, ex.Message.Length)]}");
        }
        finally
        {
            // A probe that timed out mid-generation leaves the session running
            // upstream — abort it. (A serve-side failure needs no abort.)
            if (session is not null)
            {
                try { await engine.AbortAsync(session, ProbeDir, CancellationToken.None); }
                catch { /* already gone */ }
            }
        }
    }
}
