namespace AgentFactory.App.Fleet;

/// <summary>
/// The session path the executors drive, one implementation per executable
/// flavour: the opencode flavour over the server-first contract
/// (<see cref="OpenCodeEngine"/>). A phase's pin picks the engine; unpinned
/// phases take the opencode engine — engine defaults, byte-for-byte the
/// pre-profiles behavior.
/// </summary>
public interface IAgentEngine
{
    /// <summary>Begin a session scoped to a directory; returns the id the
    /// executor records. A pinned <paramref name="model"/> is the palette-form
    /// string the flavour accepts (provider-scoped for every flavour so far);
    /// null runs the engine's defaults.</summary>
    Task<string> CreateSessionAsync(string directory, string title, string? model, CancellationToken ct = default);

    /// <summary>Send the prompt and await the final reply, with the engine's
    /// cost/token readback and the model it actually ran (truth over intent;
    /// null when the engine cannot read a model back).</summary>
    Task<SessionResult> RunPromptAsync(string sessionId, string directory, string prompt, CancellationToken ct = default);

    /// <summary>Every skill the engine discovers for a directory — the run
    /// start's fail-fast truth (ticket 012). Null when discovery is
    /// unavailable; the caller falls back to the library on disk.</summary>
    Task<List<string>?> ListSkillsAsync(string directory, CancellationToken ct = default);

    /// <summary>Inject a profile key. Adapter-less flavours never reach this
    /// path; flavours whose auth flows from their own store would accept and
    /// ignore the call — native login wins (ticket 025).</summary>
    Task InjectAuthAsync(string providerId, string key, CancellationToken ct = default);
}

/// <summary>Flavour → engine resolution. Flavours without an
/// adapter have no entry here — the run-start executability check keeps them
/// off this path, so the throw below is a bug guard, not a run path.</summary>
public sealed class EngineResolver(OpenCodeEngine opencode)
{
    /// <summary>The engine unpinned phases have always run on.</summary>
    public OpenCodeEngine Default => opencode;

    public IAgentEngine For(AgentFlavour flavour) => flavour switch
    {
        AgentFlavour.OpencodeGo => opencode,
        _ => throw new InvalidOperationException(
            $"flavour {AgentFlavours.Label(flavour)} has no execution adapter"),
    };
}
