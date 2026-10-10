namespace AgentFactory.Api;

using System.Text.Json.Serialization;

/// <summary>
/// What <c>GET /api/credentials</c> returns: every credential the factory can use, each
/// as a name and whether it has a value. Property names are pinned camelCase because the
/// JSON is a contract with a separate app (#49).
/// </summary>
/// <remarks>
/// There is deliberately no value here and there cannot be one. The credentials surface
/// is write-only by design (#32): a name travels, presence travels, and the value goes in
/// and is never rendered back. The endpoint reaches the reader through
/// <c>CompositeCredentialReader.Has</c> only — never <c>Read</c>, which keeps exactly its
/// two callers — so a value has no path onto this response at all.
/// </remarks>
public sealed record CredentialsView(
    [property: JsonPropertyName("credentials")] IReadOnlyList<CredentialView> Credentials);

/// <summary>One credential's name and whether it has a value, from either source.</summary>
public sealed record CredentialView(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("set")] bool Set);

/// <summary>The body of a credential write: the value, and nothing else.</summary>
public sealed record CredentialWrite(
    [property: JsonPropertyName("value")] string? Value);
