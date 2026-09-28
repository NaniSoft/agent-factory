namespace AgentFactory.Credentials;

/// <summary>
/// Where a secret comes from. One method, and it is the whole of the factory's reach into
/// the host's environment — which is worth being small, because it is the only thing in
/// the process that can put a value rather than a name into a worker container.
/// </summary>
/// <remarks>
/// It exists so that "the LLM credential goes in and the GitHub credential does not" is a
/// thing a test can assert rather than a thing a reader has to trust. Every other part of
/// the factory takes credentials by name and hands them on as names; this is the one
/// boundary at which a name becomes a value, and it belongs to the round that is starting.
/// </remarks>
public interface ICredentialReader
{
    /// <summary>
    /// The value of the environment variable with this name, or null when the environment
    /// does not have one. Null is the honest answer for a rotation that has not happened
    /// yet, and is never treated as an empty string.
    /// </summary>
    string? Read(string name);
}
