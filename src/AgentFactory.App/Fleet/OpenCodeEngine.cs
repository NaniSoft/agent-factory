using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentFactory.App.Fleet;

/// <summary>
/// Drives the host-owned `opencode serve` process over HTTP (integration
/// contract, ticket 001): sessions are directory-scoped via the `directory`
/// query parameter, prompts go through the synchronous
/// POST /session/:id/message (response carries the assistant parts), and the
/// process is killed by PID only. Never reads opencode.db or the shared log.
/// Both opencode flavours ride this one engine (<see cref="IAgentEngine"/>,
/// ticket 032) — the flavour differs only in credentials and catalog.
/// </summary>
public sealed class OpenCodeEngine : IAsyncDisposable, IAgentEngine
{
    private readonly ILogger<OpenCodeEngine> _logger;
    private readonly FleetOptions _options;
    private readonly HttpClient _http;
    private readonly object _startLock = new();
    private Process? _process;
    private string _password = "";

    public OpenCodeEngine(ILogger<OpenCodeEngine> logger, FleetOptions options)
    {
        _logger = logger;
        _options = options;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Start the server process (idempotent) and wait for health.
    /// A stale listener left by a hard-killed predecessor is reclaimed first
    /// (ticket 023) — the health gate can't tell a foreign 401 from a slow
    /// boot, so the port is known-free before we spawn.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        lock (_startLock)
        {
            if (IsRunning) return;
        }

        await PortOwnership.ReclaimAsync(_options.ServerPort, _logger, ct);

        lock (_startLock)
        {
            if (IsRunning) return;
            if (_process is not null)
            {
                _logger.LogWarning("opencode server exited (code {Code}); restarting", _process.ExitCode);
                _process.Dispose();
                _process = null;
            }

            _password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
            var exe = ToolLocator.Resolve("opencode");
            _process = Process.Start(new ProcessStartInfo(exe,
                $"serve --port {_options.ServerPort} --hostname 127.0.0.1 --print-logs --log-level INFO")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
                EnvironmentVariables =
                {
                    ["OPENCODE_SERVER_PASSWORD"] = _password,
                },
            }) ?? throw new InvalidOperationException("failed to start opencode serve");
            _processAuth = new Dictionary<string, string>(_storeAuth);

            _ = Task.Run(async () =>
            {
                try
                {
                    while (_process is { HasExited: false } && await _process.StandardError.ReadLineAsync() is { } line)
                        _logger.LogDebug("opencode: {Line}", line);
                }
                catch { /* process gone; nothing to drain */ }
            }, CancellationToken.None);
        }

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"opencode:{_password}")));

        // Readiness gate: GET /global/health (sub-2s cold boot observed; allow 30s).
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (IsRunning && await IsHealthyAsync(ct)) return;
            await Task.Delay(500, ct);
        }
        throw new InvalidOperationException(
            $"opencode server did not become healthy on port {_options.ServerPort} " +
            $"(process exited: {_process?.HasExited})");
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            // Each probe is bounded (3s): the client's overall timeout is
            // infinite, and one unlucky connection — accepted by the listen
            // backlog while the serve is still booting — otherwise hangs this
            // probe forever, silently stalling the whole fleet's boot gate
            // (seen live 2026-10-29).
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await _http.GetAsync(
                $"http://127.0.0.1:{_options.ServerPort}/global/health", timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // this probe timed out — the serve just isn't ready yet
        }
    }

    /// <summary>Create a session scoped to a worktree directory; returns the session id.
    /// A pinned <paramref name="model"/> (palette form `provider/model`, ticket 030)
    /// rides the create body per ticket 027 (`{title, model:{providerID, id}}`);
    /// null sends exactly the pre-profiles payload.</summary>
    public async Task<string> CreateSessionAsync(string directory, string title, string? model = null, CancellationToken ct = default)
    {
        object body;
        if (model is null)
        {
            body = new { title };
        }
        else if (ModelRef.TryParse(model, out var pinned))
        {
            body = new { title, model = new { providerID = pinned.ProviderId, id = pinned.ModelId } };
        }
        else
        {
            throw new InvalidOperationException(
                $"model '{model}' is not provider-scoped — expected provider/model");
        }
        using var response = await PostAsync(
            $"/session?directory={Uri.EscapeDataString(directory)}", JsonSerializer.Serialize(body), ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("session response had no id");
    }

    /// <summary>Inject a profile credential for an opencode provider (tickets
    /// 027/042): <c>PUT /auth/{providerId}</c>, honored by the running serve
    /// without a restart. The credential's shape picks the payload: an oauth
    /// envelope object rides verbatim, anything else PUTs as
    /// <c>{type:"api", key}</c>. Blank credentials never reach here (native
    /// login wins, ticket 025).</summary>
    /// <summary>Inject a profile credential into the serve's credential store
    /// (tickets 027/042): <c>PUT /auth/{providerId}</c>. The credential's
    /// shape picks the payload: an oauth envelope object rides verbatim,
    /// anything else PUTs as <c>{type:"api", key}</c>. NOTE: this writes the
    /// store only — auth is <b>boot-time</b> for execution (proven live,
    /// ticket 042: a PUT the running serve didn't boot with changes nothing;
    /// the palette likewise), so executors go through
    /// <see cref="EnsureCredentialAsync"/>, which bakes the store into a fresh
    /// boot when needed.</summary>
    public async Task InjectAuthAsync(string providerId, string credential, CancellationToken ct = default)
    {
        if (CredentialShape.OauthEnvelope(credential) is { } envelope)
        {
            await SendAsync(HttpMethod.Put, $"/auth/{providerId}", envelope, ct);
        }
        else
        {
            await SendAsync(HttpMethod.Put, $"/auth/{providerId}",
                JsonSerializer.Serialize(new { type = "api", key = credential }), ct);
        }
        _storeAuth[providerId] = credential;
    }

    /// <summary>Credentials we have PUT since the app started — provider →
    /// credential the serve's credential store provably holds. The engine
    /// never reads opencode.db directly (ticket 001); its own PUTs are the
    /// only store writes it can know about.</summary>
    private readonly Dictionary<string, string> _storeAuth = new();

    /// <summary>Credentials the RUNNING serve booted with — snapshotted from
    /// <see cref="_storeAuth"/> at each spawn. Auth is boot-time (ticket 042):
    /// this is what decides whether a PUT still needs a restart to take
    /// effect for execution.</summary>
    private Dictionary<string, string> _processAuth = new();

    /// <summary>Make the running serve able to execute on this credential
    /// (ticket 042): the credential is PUT into the store (shape-sniffed —
    /// see <see cref="InjectAuthAsync"/>), and, because auth is boot-time,
    /// the serve restarts when it did not boot with this exact credential.
    /// Steady state — same Secret, serve already baked — is PUT-only; a first
    /// or changed credential costs one restart. Executors call this at run
    /// start, before any session exists, so the restart cannot kill in-flight
    /// work (fleet concurrency is 1, carried). Credentials that live only in
    /// the host's native store (desktop login, blank profile key) never pass
    /// through here — native login wins without our involvement (025).</summary>
    public async Task EnsureCredentialAsync(string providerId, string credential, CancellationToken ct = default)
    {
        await InjectAuthAsync(providerId, credential, ct);
        if (!_processAuth.TryGetValue(providerId, out var held) || held != credential)
        {
            _logger.LogInformation(
                "credential for {Provider} is not live in the running serve (auth is boot-time, ticket 042) — restarting to bake it",
                providerId);
            await RestartAsync(ct);
        }
    }

    /// <summary>The flavour catalogs this serve can drive (ticket 027, built in
    /// 030): <c>GET /config/providers</c> — the effective provider set computed
    /// at serve boot, read from the very serve process that runs the sessions
    /// so palette and execution can never disagree. Model ids are the
    /// <c>models{}</c> keys. Null when the serve is unreachable or answers in
    /// an unexpected shape — the Profiles page's probe presents that, it
    /// doesn't fall back.</summary>
    public async Task<IReadOnlyList<ProviderCatalog>?> GetConfigProvidersAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await GetAsync("/config/providers", ct);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("providers", out var providers)
                || providers.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var list = new List<ProviderCatalog>();
            foreach (var provider in providers.EnumerateArray())
            {
                List<string> models = [];
                if (provider.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.Object)
                {
                    models.AddRange(m.EnumerateObject().Select(p => p.Name));
                }
                list.Add(new ProviderCatalog(
                    provider.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    provider.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    models));
            }
            return list;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }

    public sealed record ProviderCatalog(string Id, string Name, IReadOnlyList<string> Models);

    /// <summary>Restart the serve. Both the palette and the credentials are
    /// boot-time snapshots (027's palette finding; 042's execution finding —
    /// the old "PUT is honored for execution immediately" belief was an
    /// unisolated observation: the host store had the credential at boot), so
    /// a restart is how anything PUT after boot takes effect. Callers: the
    /// Profiles page's rotation affordance (031, idle only) and run-start
    /// <see cref="EnsureCredentialAsync"/> (before the run's first session).
    /// Kills this engine's own process only.</summary>
    public async Task RestartAsync(CancellationToken ct = default)
    {
        if (_process is { } process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch { /* already gone */ }
            process.Dispose();
            _process = null;
        }
        await StartAsync(ct);
    }

    /// <summary>
    /// Send the run prompt and await the (synchronous) reply. Returns the joined
    /// assistant text. On timeout the session is aborted, not left running.
    /// </summary>
    public async Task<SessionResult> RunPromptAsync(
        string sessionId, string directory, string prompt, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            parts = new[] { new { type = "text", text = prompt } },
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(_options.SessionTimeoutMinutes));

        HttpResponseMessage response;
        try
        {
            response = await PostAsync(
                $"/session/{sessionId}/message?directory={Uri.EscapeDataString(directory)}", body, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await AbortAsync(sessionId, directory, CancellationToken.None);
            throw new TimeoutException(
                $"session {sessionId} exceeded {_options.SessionTimeoutMinutes} min and was aborted");
        }

        // Response: { info: AssistantMessage, parts: Part[] } — text parts are the reply.
        using (response)
        using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)))
        {
            var text = string.Join("\n\n",
                doc.RootElement.GetProperty("parts").EnumerateArray()
                    .Where(p => p.GetProperty("type").GetString() == "text")
                    .Select(p => p.GetProperty("text").GetString() ?? ""));
            var session = await GetSessionAsync(sessionId, directory, ct);
            return new SessionResult(sessionId, text, session.Cost, session.TokensIn, session.TokensOut,
                session.ModelId, session.ProviderId);
        }
    }

    public sealed record SessionInfo(decimal Cost, long TokensIn, long TokensOut, string? ModelId, string? ProviderId);

    /// <summary>Session row for the dashboard (GET /session/:id shape, ticket 001).</summary>
    public sealed record SessionSummary(
        string Id,
        string Title,
        string Directory,
        string Agent,
        string ModelId,
        decimal Cost,
        long TokensIn,
        long TokensOut,
        DateTimeOffset Updated);

    /// <summary>Fetch a session by id alone — worktree directories may already be gone.</summary>
    public async Task<SessionSummary?> GetSessionByIdAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            using var response = await GetAsync($"/session/{sessionId}", ct);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            return new SessionSummary(
                root.GetProperty("id").GetString() ?? sessionId,
                root.GetProperty("title").GetString() ?? "",
                root.GetProperty("directory").GetString() ?? "",
                root.TryGetProperty("agent", out var agent) ? agent.GetString() ?? "" : "",
                root.TryGetProperty("model", out var model) ? model.GetProperty("id").GetString() ?? "" : "",
                root.GetProperty("cost").GetDecimal(),
                root.GetProperty("tokens").GetProperty("input").GetInt64(),
                root.GetProperty("tokens").GetProperty("output").GetInt64(),
                DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("time").GetProperty("updated").GetInt64()));
        }
        catch (HttpRequestException)
        {
            return null; // session gone or engine restarting
        }
    }

    /// <summary>Transcript JSON (message array) by id alone; null when unavailable.</summary>
    public async Task<string?> GetTranscriptByIdAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            using var response = await GetAsync($"/session/{sessionId}/message", ct);
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Cost/token totals for a session (GET /session/:id shape, ticket 001),
    /// plus the model the engine actually ran (ticket 030 — null when unpinned:
    /// the field reads back undefined).</summary>
    public async Task<SessionInfo> GetSessionAsync(string sessionId, string directory, CancellationToken ct = default)
    {
        using var response = await GetAsync(
            $"/session/{sessionId}?directory={Uri.EscapeDataString(directory)}", ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var cost = root.GetProperty("cost").GetDecimal();
        var tokens = root.GetProperty("tokens");
        string? modelId = null, providerId = null;
        if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object)
        {
            modelId = model.TryGetProperty("id", out var id) ? id.GetString() : null;
            providerId = model.TryGetProperty("providerID", out var pid) ? pid.GetString() : null;
        }
        return new SessionInfo(cost,
            tokens.GetProperty("input").GetInt64(),
            tokens.GetProperty("output").GetInt64(),
            modelId, providerId);
    }

    /// <summary>Full transcript replay (message list) for the dashboard / debugging.</summary>
    public async Task<string> GetTranscriptAsync(string sessionId, string directory, CancellationToken ct = default)
    {
        using var response = await GetAsync(
            $"/session/{sessionId}/message?directory={Uri.EscapeDataString(directory)}", ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task AbortAsync(string sessionId, string directory, CancellationToken ct = default)
    {
        using var response = await PostAsync(
            $"/session/{sessionId}/abort?directory={Uri.EscapeDataString(directory)}", "{}", ct);
    }

    /// <summary>
    /// Every skill the engine discovers for a directory (GET /skill?directory=,
    /// ticket 012's probe — the exact truth a session there will see). Null when
    /// the endpoint is unavailable or an unexpected shape: the caller falls back
    /// to a filesystem read of the skill library.
    /// </summary>
    public async Task<List<string>?> ListSkillsAsync(string directory, CancellationToken ct = default)
    {
        try
        {
            using var response = await GetAsync(
                $"/skill?directory={Uri.EscapeDataString(directory)}", ct);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array) return Names(root);
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    return Names(data);
                if (root.TryGetProperty("skills", out var skills) && skills.ValueKind == JsonValueKind.Array)
                    return Names(skills);
            }
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return null;
        }

        static List<string> Names(JsonElement array) =>
            [.. array.EnumerateArray()
                .Select(s => s.TryGetProperty("name", out var n) ? n.GetString() : null)
                .OfType<string>()];
    }

    private Task<HttpResponseMessage> PostAsync(string path, string json, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, path, json, ct);

    private Task<HttpResponseMessage> GetAsync(string path, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, path, null, ct);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method,
            $"http://127.0.0.1:{_options.ServerPort}{path}");
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(CancellationToken.None);
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException(
                $"{request.Method} {path} -> {status}: {detail[..Math.Min(500, detail.Length)]}");
        }
        return response;
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        if (_process is { } process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch { /* already gone */ }
            process.Dispose();
        }
    }
}
