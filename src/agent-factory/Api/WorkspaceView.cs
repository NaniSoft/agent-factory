namespace AgentFactory.Api;

using System.Text.Json.Serialization;
using AgentFactory.Workspaces;

/// <summary>
/// A review workspace as the app reads it: whether one is open, where it is, how long
/// it has left, and — when it could not be opened — the factory's own words for why.
/// It is <see cref="ReviewWorkspaces.View"/> serialised, and the app re-decides nothing
/// about it: the URL is the loopback address the factory published, the remaining time
/// is the fixed lifetime less what the clock says, and an error is a rendered state
/// rather than silence.
/// </summary>
/// <remarks>
/// <para>
/// The property names are pinned with <see cref="JsonPropertyNameAttribute"/> rather
/// than left to a naming policy, because the JSON is a contract with a separate app: a
/// web default that quietly changed casing would break the renderer without failing a
/// build. They are camelCase so the TypeScript view model and this record agree by name.
/// </para>
/// <para>
/// The remaining time travels as whole <em>seconds</em> rather than as the
/// <see cref="TimeSpan"/> the factory holds. A span has no JSON shape of its own, and
/// the number the app renders a countdown from is the number the factory already
/// computed — clamping a workspace the sweep has not yet taken to zero rather than
/// sending the renderer a negative it would have to decide about.
/// </para>
/// </remarks>
public sealed record WorkspaceView(
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("remainingSeconds")] int? RemainingSeconds,
    [property: JsonPropertyName("error")] string? Error)
{
    /// <summary>One workspace view, read the way the card and the endpoint read it.</summary>
    public static WorkspaceView Of(ReviewWorkspaces.View view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new WorkspaceView(
            view.Active,
            view.Url,
            view.Remaining is { } remaining
                ? (int)Math.Max(0, Math.Floor(remaining.TotalSeconds))
                : null,
            view.Error);
    }
}
