namespace AgentFactory.Infrastructure.WorkItems;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

/// <summary>
/// What <c>dotnet ef</c> uses to build the model at design time — and it is here so that
/// `migrations add` never boots the application and never touches a real database.
/// </summary>
/// <remarks>
/// <para>
/// Without this, the tool's only way to get a context is to run the startup project, which
/// means running <c>Program</c>: the composition root builds the store, the store opens the
/// database, and a build-time command has created and written <c>data/agent-factory.db</c>
/// before anybody decided anything. This factory makes the tool's input a model rather than
/// a running process, which is the only shape in which generating a migration is safe to do
/// on a machine that also has a real database on it.
/// </para>
/// <para>
/// **The connection string here is nearly vestigial, and deliberately.** Generating a
/// migration and writing a script are acts over the model and connect to nothing; the only
/// command that would connect is <c>database update</c>, and the factory applies its own
/// migrations at start — <c>Database.Migrate()</c> in the store, which is the same thing —
/// so nothing in this repository's workflow calls it. An in-memory database is therefore
/// the default, because a design-time default that writes a file is a stray file in
/// everybody's working copy. A human who wants to drive the schema by hand sets
/// <c>AGENT_FACTORY_DB</c> to a path, or to a whole connection string.
/// </para>
/// </remarks>
public sealed class FactoryDbContextFactory : IDesignTimeDbContextFactory<FactoryDbContext>
{
    /// <summary>
    /// The environment variable a human sets to point design-time commands at a real file.
    /// It takes a path, or a connection string when the value already names a `Data
    /// Source`.
    /// </summary>
    public const string DatabaseVariable = "AGENT_FACTORY_DB";

    public FactoryDbContext CreateDbContext(string[] args)
    {
        var configured = Environment.GetEnvironmentVariable(DatabaseVariable);
        var connection = string.IsNullOrWhiteSpace(configured) ? "Data Source=:memory:"
            : configured.Contains("Data Source", StringComparison.OrdinalIgnoreCase) ? configured
            : $"Data Source={configured}";

        return new FactoryDbContext(new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlite(connection)
            .Options);
    }
}
