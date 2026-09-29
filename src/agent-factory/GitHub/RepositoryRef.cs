namespace AgentFactory.GitHub;

using AgentFactory.Failures;

/// <summary>
/// A repository, as the two halves of the GitHub path the API addresses it by, and the
/// address the branch is pushed to.
/// </summary>
/// <remarks>
/// <para>
/// Owner and name come from the last two segments of the project's own <c>repo.url</c>,
/// which is the one address the project file declares and the one the round's container
/// cloned from. Nothing is constructed out of thin air: the push goes to the URL the
/// project file gave, so a project on a host other than github.com, or behind a path
/// prefix, is pushed to where it said it was.
/// </para>
/// <para>
/// A local path is accepted as well as an http URL, and that is not the project loader
/// having loosened: <see cref="Projects.ProjectFileLoader"/> refuses anything that is not
/// an absolute http or https URL long before a project record exists, so no project can
/// reach this with a path. It is here because a test has to be able to name a bare
/// repository on this machine, or the push — the step whose failure modes are the whole
/// of this ticket's argument — could only be tested against the real github.com.
/// <c>ProjectFileTests</c> still holds the loader to http and https, which is where that
/// rule lives.
/// </para>
/// </remarks>
public sealed record RepositoryRef(string Owner, string Name, string PushUrl)
{
    /// <summary>The API path for this repository, with each segment escaped.</summary>
    public string ApiPath => $"repos/{Uri.EscapeDataString(Owner)}/{Uri.EscapeDataString(Name)}";

    /// <summary>The two halves as GitHub writes them, which is what a head filter wants.</summary>
    public string FullName => $"{Owner}/{Name}";

    /// <summary>
    /// The project's own repository URL, read as a GitHub address. Refused rather than
    /// guessed at: a URL that names no repository is a project file that cannot be
    /// addressed, and there is nothing about a second attempt that would change that.
    /// </summary>
    public static RepositoryRef Parse(string repoUrl)
    {
        if (!TryParse(repoUrl, out var parsed))
        {
            throw new PermanentFailure(
                $"'{repoUrl}' does not name a repository the factory can push to: an address needs at least "
                    + "an owner and a repository name in its path");
        }

        return parsed;
    }

    /// <summary>
    /// The same read without the refusal, for deciding whether two addresses are the same
    /// repository — a work item's recorded URL and the one a caller passed in, which
    /// differ by a trailing <c>.git</c> or a trailing slash as often as not.
    /// </summary>
    public static bool TryParse(string? repoUrl, out RepositoryRef parsed)
    {
        parsed = default!;

        if (string.IsNullOrWhiteSpace(repoUrl))
        {
            return false;
        }

        var trimmed = repoUrl.Trim().TrimEnd('/');

        // `.git` is how a clone URL spells the same repository as a browsable one, and
        // which of the two a project file uses is its own business.
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^".git".Length];
        }

        var segments = trimmed.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        var owner = segments[^2];
        var name = segments[^1];

        if (owner.Length == 0 || name.Length == 0)
        {
            return false;
        }

        parsed = new RepositoryRef(owner, name, repoUrl.Trim());
        return true;
    }

    /// <summary>Whether two addresses are the same repository, however they were spelled.</summary>
    public static bool IsSame(string? left, string? right) =>
        TryParse(left, out var one) && TryParse(right, out var other)
        && string.Equals(one.Owner, other.Owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(one.Name, other.Name, StringComparison.OrdinalIgnoreCase);
}
