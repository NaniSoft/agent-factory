namespace AgentFactory.App.Fleet;

/// <summary>
/// The host-side skill library (ticket 012): <c>~/.agents/skills</c> as-is.
/// Used as the filesystem fallback when the engine's directory-scoped
/// <c>GET /skill</c> can't be reached — same set by construction.
/// </summary>
public static class SkillLibrary
{
    public static List<string> NamesOnDisk()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agents", "skills");
        try
        {
            return [.. Directory.EnumerateDirectories(root)
                .Where(d => File.Exists(Path.Combine(d, "SKILL.md")))
                .Select(Path.GetFileName)
                .OfType<string>()];
        }
        catch (IOException)
        {
            return []; // library unreadable: nothing to check against
        }
    }
}
