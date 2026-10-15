namespace AgentFactory.Infrastructure;

using AgentFactory.Application;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Reads <see cref="FactoryOptions"/> out of the host's configuration, which is the half
/// of the options story that is not a value.
/// <para>
/// <see cref="FactoryOptions"/> itself is what the factory is told and is
/// <c>Application</c>'s, and nothing in the policy reads a configuration key or asks where
/// the root is. Reading <c>IConfiguration</c> and walking up a disk for
/// <c>factories/</c> are this project's, and this is where the two meet. It is called
/// once, by the composition root, which is why the split falls at this method and not
/// anywhere else.
/// </para>
/// </summary>
public static class FactorySettings
{
    public static FactoryOptions FromConfiguration(IConfiguration configuration, string contentRoot)
    {
        var root = FactoryPaths.ResolveRoot(configuration, contentRoot);

        return new FactoryOptions(
            FactoriesDirectory: configuration["Factory:FactoriesDirectory"] ?? Path.Combine(root, "factories"),
            DatabasePath: configuration["Factory:DatabasePath"] ?? Path.Combine(root, "data", "agent-factory.db"),
            BoardUrl: configuration["Factory:BoardUrl"] is { Length: > 0 } url ? new Uri(url) : FactoryOptions.DefaultBoardUrl,
            AutoMerge: configuration["Factory:AutoMerge"] is { Length: > 0 } autoMerge
                && autoMerge.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            TemporalEndpoint = configuration["Factory:Temporal:Endpoint"],
        };
    }
}