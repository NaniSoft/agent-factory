using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentFactory.App.Fleet;

/// <summary>
/// Which coding agent — and credential mode — a profile drives (ticket 028):
/// the opencode CLI behind the single kept flavour. A new flavour arrives as
/// code, not config.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentFlavour
{
    /// <summary>opencode go — the flat-rate subscription (provider `opencode-go`).</summary>
    OpencodeGo,
}

/// <summary>Flavour mechanics the engine and stores agree on (tickets 026/027).</summary>
public static class AgentFlavours
{
    /// <summary>The opencode provider id a flavour's model ids are scoped to
    /// (`opencode-go/…`).</summary>
    public static string? OpencodeProvider(AgentFlavour flavour) => flavour switch
    {
        AgentFlavour.OpencodeGo => "opencode-go",
        _ => null,
    };

    /// <summary>Whether the engine can actually run sessions for the flavour
    /// today (the store refuses new utility bindings it can't honor).</summary>
    public static bool Executable(AgentFlavour flavour) => flavour switch
    {
        _ => true,
    };

    public static string Label(AgentFlavour flavour) => flavour switch
    {
        AgentFlavour.OpencodeGo => "opencode-go",
        _ => flavour.ToString(),
    };
}

/// <summary>
/// A named configuration for driving one flavour (ticket 028): an optional
/// API key — blank means the agent's own stored login wins (ticket 025) — and
/// the model palette that bounds every phase pin. Several profiles may share
/// a flavour (spare keys, cheap tiers).
/// </summary>
public sealed class AgentProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public AgentFlavour Flavour { get; set; } = AgentFlavour.OpencodeGo;
    public string ApiKey { get; set; } = "";

    /// <summary>The model palette: the strings each flavour accepts as model
    /// ids (provider-scoped `provider/model` for the opencode flavours). A
    /// phase pin must be a palette member — validated at run start (030) and
    /// at save (031).</summary>
    public List<string> Models { get; set; } = [];

    public AgentProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Flavour = Flavour,
        ApiKey = ApiKey,
        Models = [.. Models],
    };
}

/// <summary>Stored profile credentials reach the engine through one resolver
/// (ticket 042): the value may reference environment variables as
/// <c>{env:NAME}</c> anywhere in the string — the Secret path (pod env →
/// profile → per-run PUT). A missing variable resolves empty, which callers
/// treat exactly like an unset key (native login wins, ticket 025), so one
/// stored document serves both the host (no such env — desktop login) and
/// the cluster (Secret env injected at deploy).</summary>
public static partial class ProfileCredential
{
    [GeneratedRegex(@"\{env:([^{}]+)\}")]
    private static partial Regex EnvRef();

    /// <summary>The credential to inject: <paramref name="stored"/> with
    /// every <c>{env:NAME}</c> replaced by its environment value (missing →
    /// empty). Empty input, or references that all resolve empty, yield ""
    /// — nothing to inject.</summary>
    public static string Resolve(string? stored) =>
        string.IsNullOrEmpty(stored) ? ""
        : EnvRef().Replace(stored, m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? "");
}

/// <summary>What a stored profile credential can be (ticket 042): a static
/// api key, or an opencode OAuth envelope — <c>{type:"oauth", refresh,
/// access, expires, metadata}</c>, the exact shape opencode keeps in its
/// credential store. The shape is sniffed from the value itself, so a profile
/// keeps one masked key field whichever shape it holds.</summary>
public static class CredentialShape
{
    /// <summary>The trimmed envelope text when <paramref name="credential"/>
    /// is an oauth envelope JSON object, else null — including for values
    /// that parse as JSON but aren't oauth objects (those ride the api-key
    /// path; the gateway judges them).</summary>
    public static string? OauthEnvelope(string? credential)
    {
        if (string.IsNullOrWhiteSpace(credential)) return null;
        var trimmed = credential.Trim();
        if (!trimmed.StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "oauth"
                    ? trimmed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The token a bearer probe presents for a credential (ticket
    /// 034's key test): an envelope's <c>access</c> field, else the
    /// credential itself.</summary>
    public static string BearerToken(string credential) =>
        OauthEnvelope(credential) is { } envelope
        && JsonNode.Parse(envelope) is { } node
        && node["access"] is JsonValue access
        && access.TryGetValue<string>(out var token)
            ? token
            : credential;
}

/// <summary>
/// The fleet-level profile-and-model that triage and parent planning sessions
/// run on (ticket 028) — never pipeline phases. Null fields = engine defaults,
/// exactly the pre-profiles behavior.
/// </summary>
public sealed class UtilityBinding
{
    public string? ProfileId { get; set; }
    public string? ModelId { get; set; }

    public UtilityBinding Clone() => new() { ProfileId = ProfileId, ModelId = ModelId };
}

/// <summary>A palette model id split into the provider-scoped pair the opencode
/// serve API takes (`POST /session {model:{providerID, id}}`, ticket 027):
/// `opencode/claude-sonnet-5` → (`opencode`, `claude-sonnet-5`). Split on the
/// first slash — ids may themselves carry slashes.</summary>
public sealed record ModelRef(string ProviderId, string ModelId)
{
    public static bool TryParse(string? model, out ModelRef parsed)
    {
        parsed = new ModelRef("", "");
        if (string.IsNullOrWhiteSpace(model)) return false;
        var slash = model.IndexOf('/');
        if (slash <= 0 || slash == model.Length - 1) return false;
        parsed = new ModelRef(model[..slash], model[(slash + 1)..]);
        return true;
    }
}

/// <summary>
/// Per-flavour facts and catalogs the Profiles page presents (ticket 031,
/// wording from 029's prototype). The opencode flavour enumerates live
/// (`GET /config/providers`, filtered — the page fetches).
/// </summary>
public static class FlavourFacts
{
    /// <summary>The engine binary a profile's flavour drives.</summary>
    public static string Engine(AgentFlavour flavour) => flavour switch
    {
        AgentFlavour.OpencodeGo => "opencode",
        _ => "opencode",
    };

    /// <summary>What the API-key field means for this flavour (025/028, env
    /// references since 042).</summary>
    public static string KeyMode(AgentFlavour flavour) => flavour switch
    {
        AgentFlavour.OpencodeGo =>
            "optional — literal key, oauth envelope, or {env:VAR}; blank = opencode's own stored login wins",
        _ => "optional — literal key, oauth envelope, or {env:VAR}; blank = opencode's own stored login wins",
    };

    /// <summary>Where the palette list comes from, shown as the source note.</summary>
    public static string CatalogNote(AgentFlavour flavour) => flavour switch
    {
        AgentFlavour.OpencodeGo =>
            "live · GET /config/providers, filtered to opencode-go (boot-time snapshot)",
        _ => "live · GET /config/providers, filtered to opencode-go (boot-time snapshot)",
    };

    /// <summary>What the catalog looks like with no key, for the probe's
    /// blank-key advisory.</summary>
    public static string KeylessNote(AgentFlavour flavour) => flavour switch
    {
        AgentFlavour.OpencodeGo => "absent from the catalog until the subscription credential exists",
        _ => "absent from the catalog until the subscription credential exists",
    };

    /// <summary>The one-line flavour fact in the right-hand pane.</summary>
    public static string Fact(AgentFlavour flavour) => flavour switch
    {
        AgentFlavour.OpencodeGo => "flat-rate subscription — ~30 open-weight models; overage falls back to PAYG credits",
        _ => "flat-rate subscription — ~30 open-weight models; overage falls back to PAYG credits",
    };

    /// <summary>The fixed catalog for flavours without an enumeration
    /// endpoint. The opencode flavour ignores this — theirs is live.</summary>
    public static IReadOnlyList<string> StaticCatalog(AgentFlavour flavour) => flavour switch
    {
        _ => [],
    };

    /// <summary>The catalog a flavour's palette multi-select offers: the
    /// caller-fetched live snapshot for the opencode flavour (null = serve
    /// unreachable — the page shows the honest unavailable state). Live entries
    /// are re-scoped to the palette's canonical `provider/model` form (028):
    /// the serve lists bare model ids under each provider, while palette members
    /// and phase pins carry the provider prefix that `ModelRef` splits for dispatch.</summary>
    public static IReadOnlyList<string> Catalog(
        AgentFlavour flavour, IReadOnlyList<OpenCodeEngine.ProviderCatalog>? live)
    {
        if (AgentFlavours.OpencodeProvider(flavour) is not { } providerId) return StaticCatalog(flavour);
        var provider = live?.FirstOrDefault(p => p.Id == providerId);
        return provider is null ? [] : [.. provider.Models.Select(m => $"{providerId}/{m}")];
    }

    /// <summary>The flavour's upstream models endpoint (027's research) —
    /// the one place a draft key can be tested without writing it to the
    /// shared serve. Null for flavours without a key-testable endpoint.</summary>
    public static string? ExternalCatalogUrl(AgentFlavour flavour) => flavour switch
    {
        AgentFlavour.OpencodeGo => "https://opencode.ai/inference/go/v1/models",
        _ => null,
    };

    /// <summary>Every model id the Go gateway offers, fetched from the docs'
    /// public models endpoint (`curl https://opencode.ai/inference/go/v1/models` —
    /// no auth needed to list). Returned bare (`deepseek-v4.1-flash`); the palette
    /// scopes them to `opencode-go/…`. Empty when the gateway is unreachable —
    /// callers fall back to the serve snapshot.</summary>
    public static async Task<IReadOnlyList<string>> FetchExternalModelsAsync(
        AgentFlavour flavour, CancellationToken ct = default)
    {
        var url = ExternalCatalogUrl(flavour);
        if (url is null) return [];
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentFactory/1.0");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return [];
            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.Array)
                return [];
            var ids = new List<string>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String
                    && id.GetString() is { Length: > 0 } bare)
                    ids.Add(bare);
            }
            ids.Sort(StringComparer.Ordinal);
            return ids;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>Key-test verdict for the probe (ticket 034): Definitive means
    /// the gateway answered authoritatively (2xx = accepted, 401/403 =
    /// rejected); anything else is an honest "no verdict", never a guess.</summary>
    public sealed record KeyTest(bool Definitive, bool Accepted, string Detail);

    /// <summary>Tests a draft credential against the flavour's upstream
    /// catalog with a bearer GET — read-only, aimed at the gateway, never at
    /// the shared serve, so a bad paste can't disturb stored credentials or
    /// in-flight sessions. An oauth envelope presents its access token
    /// (ticket 042); the refresh token never leaves the envelope.</summary>
    public static async Task<KeyTest> ProbeKeyAsync(AgentFlavour flavour, string key)
    {
        var url = ExternalCatalogUrl(flavour);
        if (url is null)
            return new KeyTest(Definitive: false, Accepted: false, "no key-test endpoint for this flavour");
        var host = new Uri(url).Host;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", CredentialShape.BearerToken(key));
        try
        {
            using var response = await http.SendAsync(request);
            return response.StatusCode switch
            {
                System.Net.HttpStatusCode.OK =>
                    new KeyTest(Definitive: true, Accepted: true, $"key accepted by {host}"),
                System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                    new KeyTest(Definitive: true, Accepted: false,
                        $"key REJECTED by {host} (HTTP {(int)response.StatusCode}) — a run on this key fails its messages with APIError parts (027)"),
                _ => new KeyTest(Definitive: false, Accepted: false,
                    $"no verdict — {host} answered HTTP {(int)response.StatusCode}"),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new KeyTest(Definitive: false, Accepted: false,
                $"no verdict — key-test unreachable ({ex.GetType().Name})");
        }
    }
}
