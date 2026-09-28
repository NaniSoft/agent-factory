namespace AgentFactory.Containers;

/// <summary>
/// One worker container, described without saying what a round is. The image, the
/// command that runs inside it, and the environment it runs with — nothing else. A
/// container runtime that knew what a work item or an issue was would be a second
/// state machine, and the seam above it exists so the orchestrator has no concept of
/// Docker at all (ADR-0004).
/// </summary>
/// <param name="Name">
/// The name the container is created under, and how it is addressed for the rest of its
/// short life. Addressing it by name rather than by id is deliberate: a name is known
/// before the container exists and after an id has been lost, so the unconditional
/// teardown can still find it.
/// </param>
/// <param name="Image">The project's configured worker image.</param>
/// <param name="Command">
/// The arguments handed to the image's own entrypoint. Never a shell string: each
/// element is passed through verbatim, so a reviewer's words cannot become shell syntax.
/// </param>
/// <param name="Environment">
/// The round's environment. Names and values only, and a caller that has a credential to
/// hand over is putting it in the wrong place: the round holds nothing that can write to
/// a remote (ADR-0006).
/// </param>
public sealed record WorkerContainerRequest(
    string Name,
    string Image,
    IReadOnlyList<string> Command,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>
/// What one worker container left behind on the host. Both paths are host paths the
/// runtime lifted out with <c>docker cp</c>; there is no third channel out of a round
/// (ADR-0010), and nothing here is read out of the container's streams.
/// </summary>
/// <param name="ResultFile">
/// The one result file, lifted out of the container's output directory. Null when the
/// round wrote none, which is a round that came back without a result rather than an
/// error: the log is still there, so it is never invisible.
/// </param>
/// <param name="RoundTree">
/// The round's working tree, lifted out the same way, so the host can reach the commit
/// the round made. Null when there was none to lift. The container is gone by the time
/// anyone reads this, and the host pushes (ADR-0006).
/// </param>
/// <param name="LogTail">
/// The end of what the round said, bounded. The whole log went to the caller's progress
/// channel as the round ran; this is the part worth having in a log line afterwards.
/// </param>
public sealed record WorkerContainerRun(
    string? ResultFile,
    string? RoundTree,
    string LogTail);
