namespace AgentFactory.Tests.Durability;

/// <summary>
/// The credential seam for the durable-workflow tests, on the same idiom as
/// <c>DockerFactAttribute</c>: it probes once whether a Temporal server is answering on
/// the configured endpoint, and skips with the reason stated when there is none. It does
/// not start a server and does not fake one — a workflow test against a server that is
/// not there is not a test, and the compose file is where the dev server comes from.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class TemporalFactAttribute : FactAttribute
{
    private static readonly bool Reachable = Probe().GetAwaiter().GetResult();

    public TemporalFactAttribute()
    {
        if (!Reachable)
        {
            Skip = "no Temporal server is answering on localhost:7233 — start the dev server "
                + "(docker compose up temporal) and these run";
        }
    }

    private static async Task<bool> Probe()
    {
        try
        {
            var client = await Temporalio.Client.TemporalClient.ConnectAsync(new()
            {
                TargetHost = "localhost:7233",
                Namespace = "default",
            });

            // A connect that answers is a server; asking it for anything more would make
            // the probe a test of the server rather than of whether there is one.
            return client is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
