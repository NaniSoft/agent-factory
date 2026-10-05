namespace AgentFactory.Tests.Boundary;

/// <summary>
/// A throwaway factory root on disk: a <c>factories/</c> directory of project files
/// and a <c>data/</c> directory for the SQLite file. One per test, deleted on dispose.
/// </summary>
public sealed class FactoryRoot : IDisposable
{
    private FactoryRoot(string path) => Path = path;

    public string Path { get; }

    public string FactoriesDirectory => System.IO.Path.Combine(Path, "factories");

    public string DatabasePath => System.IO.Path.Combine(Path, "data", "agent-factory.db");

    public static FactoryRoot Create()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "agent-factory-tests",
            Guid.NewGuid().ToString("n"));

        Directory.CreateDirectory(System.IO.Path.Combine(path, "factories"));
        return new FactoryRoot(path);
    }

    /// <summary>Writes a project file into <c>factories/</c> exactly as it would be committed.</summary>
    public FactoryRoot WithProjectFile(string fileName, string yaml)
    {
        File.WriteAllText(System.IO.Path.Combine(FactoriesDirectory, fileName), yaml);
        return this;
    }

    /// <summary>
    /// Writes a project file that fails validation, so the board renders its refusal
    /// section. A test about what the board says about a project file it could not read
    /// needs one there to say it about.
    /// </summary>
    public FactoryRoot WithRefusedFile(string fileName) =>
        WithProjectFile(fileName, "name: broken\nrepo:\n  url: not-a-url\n");

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(Path))
            {
                System.IO.Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}
