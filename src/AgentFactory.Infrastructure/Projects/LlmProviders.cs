namespace AgentFactory.Infrastructure.Projects;

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
/// matters to anyone; <c>opencode</c> is Zen, the gateway the CLI itself serves, and
/// <c>opencode-go</c> is the Go subscription — a separate provider in the registry the
/// CLI reads (models.dev), carrying its own catalog and its own key under the same
/// variable name. A Go key and a Zen key therefore share one credential name and one
/// secrets file: which one a round actually speaks to is decided by the model
/// reference's prefix alone, so a project runs on one or the other and never both.
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
            ["opencode-go"] = "OPENCODE_API_KEY",
        };

    /// <summary>
    /// The provider prefixes in the order a surface offers them, which is the order the
    /// table declares them: the two measured against the pinned CLI first, the gateway
    /// the CLI itself serves and its subscription sibling last.
    /// </summary>
    public static readonly IReadOnlyList<string> OrderedIds =
    [
        "anthropic",
        "openai",
        "google",
        "opencode",
        "opencode-go",
    ];

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
