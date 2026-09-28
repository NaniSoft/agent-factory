namespace AgentFactory.Projects;

/// <summary>
/// Why a file in <c>factories/</c> is not a project. A file that fails validation keeps
/// its own project out of the rotation; it never stops the factory from starting.
/// </summary>
public enum ProjectRejectionReason
{
    /// <summary>A required field is missing or empty.</summary>
    Partial,

    /// <summary>The project is shared between files, or a field in it is shared from another mapping.</summary>
    Shared,

    /// <summary>The file pulls in another file instead of standing alone.</summary>
    Included,

    /// <summary>The file is an artifact the loader derived, not a project file anyone wrote.</summary>
    Generated,

    /// <summary>The file is not a project file at all: unknown field, or a value that is not valid.</summary>
    Invalid,
}

/// <summary>One refused file, and the reason it was refused.</summary>
public sealed record ProjectFileRejection(
    string FileName,
    ProjectRejectionReason Reason,
    string Message);

/// <summary>
/// The set the factory actually serves, in rotation order. The served set is exactly
/// the files that validated in <c>factories/</c> — there is no registry to keep in
/// step with the directory, and a refused file is reported rather than swallowed.
/// </summary>
public sealed record ProjectLoadReport(
    IReadOnlyList<Project> Projects,
    IReadOnlyList<ProjectFileRejection> Rejections)
{
    public static readonly ProjectLoadReport Empty = new([], []);
}
