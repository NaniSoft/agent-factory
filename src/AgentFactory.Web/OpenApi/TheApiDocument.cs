namespace AgentFactory.OpenApi;

using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

/// <summary>
/// The OpenAPI document the Board's generated types are read from, and the one thing about
/// it that the framework infers and gets wrong for a reader (#80).
/// </summary>
/// <remarks>
/// <para>
/// The document is not served — <c>MapOpenApi()</c> is deliberately never called — and it is
/// not a feature the process offers anybody at runtime. It is the API's account of what it
/// returns, produced at build time and turned into <c>admin/lib/generated/openapi.d.ts</c>,
/// which the Board's own view model is aliased over. So the document is a contract on this
/// side of the wall and the Board is the consumer on the other.
/// </para>
/// <para>
/// The endpoints' own <c>.Produces&lt;T&gt;()</c> annotations carry the shape; this is the one
/// correction they cannot, because it is about how a type is described rather than which type
/// it is. ASP.NET Core reflects the minimal API's web JSON options into every schema, and
/// those options set <c>JsonNumberHandling.AllowReadingFromString</c> — so every <c>int</c>
/// in the document is written as <c>"integer" | "string"</c> with a pattern. That is true of
/// a request the factory would accept and false of every response it sends: the serialiser
/// writes a number, never a quoted number, and there is no int in any request body to be
/// lenient about. Left alone it would widen every count, id and line number in the Board's
/// generated types to <c>number | string</c>, which is a schema that describes the wire less
/// accurately than the hand-written types it replaces did.
/// </para>
/// <para>
/// So the string half is dropped from every integer schema, on the document only. Nothing
/// about what the API accepts changes: this is a description being corrected, not a
/// behaviour. The pattern the generator emits with the string half goes with it, because a
/// pattern that matches a quoted number has nothing left to describe.
/// </para>
/// </remarks>
internal static class TheApiDocument
{
    /// <summary>
    /// Registers the document's one correction. Called where the document is registered, so
    /// the generator and its transformer are decided in one place.
    /// </summary>
    public static void Configure(OpenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddSchemaTransformer((schema, _, _) =>
        {
            if (schema.Type is { } type && type.HasFlag(JsonSchemaType.Integer))
            {
                schema.Type = type & ~JsonSchemaType.String;
                schema.Pattern = null;
            }

            return Task.CompletedTask;
        });
    }
}
