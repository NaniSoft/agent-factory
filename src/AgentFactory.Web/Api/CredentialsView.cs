namespace AgentFactory.Api;

using System.Text.Json.Serialization;

/// <summary>
/// What <c>GET /api/credentials</c> returns: every credential the factory can use, each
/// as a name and whether it has a value, beside the LLM providers the factory knows and
/// the one environment variable each of them reads. Property names are pinned camelCase
/// because the JSON is a contract with a separate app (#49).
/// </summary>
/// <remarks>
/// There is deliberately no value here and there cannot be one. The credentials surface
/// is write-only by design (#32): a name travels, presence travels, and the value goes in
/// and is never rendered back. The endpoint reaches the reader through
/// <c>CompositeCredentialReader.Has</c> only — never <c>Read</c>, which keeps exactly its
/// two callers — so a value has no path onto this response at all. The providers are the
/// factory's own table (<c>LlmProviders</c>) rather than the app's, because which
/// environment variable a provider's tooling reads is the factory's measured fact, and a
/// second copy in the app would be a second fact free to drift.
/// </remarks>
public sealed record CredentialsView(
    [property: JsonPropertyName("credentials")] IReadOnlyList<CredentialView> Credentials,
    [property: JsonPropertyName("llmProviders")] IReadOnlyList<LlmProviderView> LlmProviders);

/// <summary>One credential's name and whether it has a value, from either source.</summary>
public sealed record CredentialView(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("set")] bool Set);

/// <summary>
/// One LLM provider a model reference may name, and the canonical environment variable
/// the agent's tooling reads the key under.
/// </summary>
public sealed record LlmProviderView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("canonicalEnvironmentVariable")] string CanonicalEnvironmentVariable);

/// <summary>The body of a credential write: the value, and nothing else.</summary>
public sealed record CredentialWrite(
    [property: JsonPropertyName("value")] string? Value);
