namespace AgentFactory.Tests.Boundary;

/// <summary>
/// Deletes a directory that may hold git object files, which arrive read-only on Windows
/// and stop a recursive delete dead. A round's tree is a repository with a packfile in it,
/// and a test that ran a real round owns one — so this is the one walk every disposer
/// shares rather than a courtesy each test restates.
/// </summary>
public static class GitTrees
{
    public static void Delete(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test over.
        }
        catch (UnauthorizedAccessException)
        {
            // As above, for a file whose attributes could not be cleared.
        }
    }
}
