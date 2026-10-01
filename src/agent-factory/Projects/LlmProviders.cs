namespace AgentFactory.Projects;

/// <summary>
/// The providers a project's <c>llm.model</c> reference may name, and the one
/// environment variable each provider's tooling actually reads.
/// </summary>
/// <remarks>
/// The model-reference decision (#38) puts this table in the factory, not the project
/// file: a project names its credential whatever it likes, and the factory re-emits the
/// value under the canonical name — because the canonical name is the agent tooling's
/// contract, not the project's, and a project asked to state it would be one typo away
/// from the silent <c>Model unavailable</c> failure the research measured. An unknown
/// provider prefix is therefore refused at load rather than carried: a round handed a
/// key under a name nothing reads is a round that says nothing about why.
/// <para>
/// <c>anthropic</c> and <c>openai</c> are measured against the pinned CLI (research on
/// branch <c>research/opencode-model-config</c>); <c>google</c> is the name in the
/// provider registry that CLI reads and is worth one measured confirmation before it
/// matters to anyone; <c>opencode</c> is the gateway the CLI itself serves — the live
/// deployment's reviewer holds a key there, and the free model it publishes is how the
/// first rounds were demonstrated.
/// </para>
/// </remarks>
public static class LlmProviders
{
    public static readonly IReadOnlyDictionary<string, string> CanonicalEnvironmentVariables =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["anthropic"] = "ANTHROPIC_API_KEY",
            ["openai"] = "OPENAI_API_KEY",
            ["google"] = "GOOGLE_GENERATIVE_AI_API_KEY",
            ["opencode"] = "OPENCODE_API_KEY",
        };

    /// <summary>
    /// The canonical environment variable for a model reference, or null when the
    /// reference is not of the form <c>provider/model</c> or names a provider this table
    /// does not know.
    /// </summary>
    public static string? CanonicalEnvironmentVariableFor(string model)
    {
        var separator = model.IndexOf('/');
        if (separator <= 0 || separator == model.Length - 1)
        {
            return null;
        }

        return CanonicalEnvironmentVariables.TryGetValue(model[..separator], out var canonical)
            ? canonical
            : null;
    }
}
