namespace AgentFactory.Credentials;

/// <summary>
/// The one <see cref="ICredentialReader"/> the process uses: the process environment
/// first, exactly as always, then the secrets directory the board's credentials form
/// writes into — one file per credential, the name is the file (#32).
/// </summary>
/// <remarks>
/// Read at use time, as the environment reader always was: a value written into the
/// directory is picked up by the next round or merge without a restart, and a round
/// already running keeps the value its container was handed. A name found in neither
/// place reads as absent, which is the one answer every caller already handles out loud.
/// </remarks>
public sealed class CompositeCredentialReader : ICredentialReader
{
    private readonly ICredentialReader _environment;
    private readonly string _secretsDirectory;

    public CompositeCredentialReader(ICredentialReader environment, string secretsDirectory)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _secretsDirectory = secretsDirectory ?? throw new ArgumentNullException(nameof(secretsDirectory));
    }

    /// <summary>Writes one credential into the secrets directory, the whole value being the file.</summary>
    public void Write(string name, string value)
    {
        Directory.CreateDirectory(_secretsDirectory);
        File.WriteAllText(Path.Combine(_secretsDirectory, name), value.TrimEnd('\r', '\n'));
    }

    /// <summary>Whether a credential has a value, by name, from either source.</summary>
    public bool Has(string name)
    {
        if (File.Exists(Path.Combine(_secretsDirectory, name)))
        {
            return true;
        }

        return _environment.Read(name) is { Length: > 0 };
    }

    public string? Read(string name)
    {
        if (_environment.Read(name) is { Length: > 0 } fromEnvironment)
        {
            return fromEnvironment;
        }

        var file = Path.Combine(_secretsDirectory, name);
        return File.Exists(file) ? File.ReadAllText(file).TrimEnd('\r', '\n') : null;
    }
}
