namespace AgentFactory;

/// <summary>
/// Finds the factory root: the directory that holds <c>factories/</c> and <c>data/</c>.
/// The solution lives in <c>src/</c>, so the root is above the content root — we walk
/// up to find it rather than hard-coding <c>../..</c>, which breaks the moment the
/// factory is published or run from a container image.
/// </summary>
public static class FactoryPaths
{
    public const string FactoriesDirectoryName = "factories";

    public static string ResolveRoot(IConfiguration configuration, string contentRoot)
    {
        if (configuration["Factory:Root"] is { Length: > 0 } configured)
        {
            return Path.GetFullPath(configured);
        }

        if (Environment.GetEnvironmentVariable("FACTORY_ROOT") is { Length: > 0 } fromEnvironment)
        {
            return Path.GetFullPath(fromEnvironment);
        }

        for (var directory = new DirectoryInfo(contentRoot); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, FactoriesDirectoryName)))
            {
                return directory.FullName;
            }
        }

        return Path.GetFullPath(contentRoot);
    }
}
