namespace AgentFactory.Tests.Boundary;

using AgentFactory.Credentials;

/// <summary>
/// The host's environment, faked. A round's container is the one place a secret's value
/// is allowed to travel, so the boundary that can produce one is faked rather than reached
/// for — a test that read the real process environment could not assert the two things
/// that matter about it: that the LLM key is handed over, and that the GitHub key never is.
/// </summary>
public sealed class FakeCredentialReader : ICredentialReader
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>Every name this was asked for, in order. The GitHub key must never be one.</summary>
    public List<string> Asked { get; } = [];

    /// <summary>The environment has a value under this name.</summary>
    public FakeCredentialReader Having(string name, string value)
    {
        _values[name] = value;
        return this;
    }

    public string? Read(string name)
    {
        Asked.Add(name);
        return _values.GetValueOrDefault(name);
    }
}
