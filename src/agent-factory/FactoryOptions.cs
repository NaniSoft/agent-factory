namespace AgentFactory;

/// <summary>
/// The factory's own settings: where the project files live, where the store lives,
/// and where the board is published. None of it is per project — project files are
/// the only per-project configuration, and there is no other.
/// </summary>
public sealed record FactoryOptions(
    string FactoriesDirectory,
    string DatabasePath,
    Uri BoardUrl)
{
    /// <summary>
    /// The board is published on port 5000 and bound to loopback. Loopback is a named
    /// compensating control, not a default: the machine is the trust boundary.
    /// </summary>
    public static readonly Uri DefaultBoardUrl = new("http://127.0.0.1:5000");

    public static FactoryOptions FromConfiguration(IConfiguration configuration, string contentRoot)
    {
        var root = FactoryPaths.ResolveRoot(configuration, contentRoot);

        return new FactoryOptions(
            FactoriesDirectory: configuration["Factory:FactoriesDirectory"] ?? Path.Combine(root, "factories"),
            DatabasePath: configuration["Factory:DatabasePath"] ?? Path.Combine(root, "data", "agent-factory.db"),
            BoardUrl: configuration["Factory:BoardUrl"] is { Length: > 0 } url ? new Uri(url) : DefaultBoardUrl);
    }
}
