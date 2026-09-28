namespace AgentFactory.Tests.Boundary;

using System.Net;
using AgentFactory;
using AgentFactory.GitHub;
using AgentFactory.Loop;
using AgentFactory.Polling;
using AgentFactory.Rounds;
using AgentFactory.WorkItems;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// The one process. It serves the board, it holds the store, and it is the whole
/// application boundary the rest of the suite drives.
/// </summary>
public class TheProcessTests
{
    [Fact]
    public async Task The_board_answers_from_the_one_process()
    {
        using var root = FactoryRoot.Create();
        await using var host = await FactoryHost.StartAsync(root);

        var response = await host.Board.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_process_starts_before_either_seam_has_an_implementation()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);

        // The composition root the entry point builds, with no seam substituted at all.
        // A registered component whose dependency cannot be resolved stops the process
        // starting in the development environment, and a factory that cannot start is
        // not a factory that can be run before the adapters land. This is the assertion
        // that it starts anyway: intake and the loop are registered and constructible,
        // and neither is driven, because there is nothing real behind them to step.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(FactoryApp).Assembly.GetName().Name,
            ContentRootPath = root.Path,
            EnvironmentName = Environments.Development,
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var app = FactoryApp.Create(
            builder,
            new FactoryOptions(root.FactoriesDirectory, root.DatabasePath, new Uri("http://127.0.0.1:0")));

        await app.StartAsync();
        try
        {
            var address = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .First();

            using var client = new HttpClient { BaseAddress = new Uri(address) };
            using var response = await client.GetAsync("/");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(app.Services.GetService<Poller>());
            Assert.NotNull(app.Services.GetService<Orchestrator>());
            Assert.Empty(app.Services.GetRequiredService<IWorkItemStore>().List());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_seam_with_nothing_behind_it_refuses_rather_than_doing_nothing()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var app = FactoryApp.Create(
            WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(FactoryApp).Assembly.GetName().Name,
                ContentRootPath = root.Path,
                EnvironmentName = Environments.Development,
            }),
            new FactoryOptions(root.FactoriesDirectory, root.DatabasePath, new Uri("http://127.0.0.1:0")));

        await using (app.ConfigureAwait(false))
        {
            // Nothing polls, nothing loops, and the reason is a refusal that says which
            // ticket brings the adapter. A seam that quietly did nothing would be worse:
            // the factory would look like it was running, and intake nothing.
            var github = app.Services.GetRequiredService<IGitHub>();
            var refusal = await Assert.ThrowsAsync<NotSupportedException>(
                () => github.ListOpenIssuesAsync("https://github.com/NaniSoft/nexus", CancellationToken.None));

            Assert.Contains("merging ticket", refusal.Message, StringComparison.Ordinal);

            var agent = app.Services.GetRequiredService<INOpenCode>();
            var round = await Assert.ThrowsAsync<NotSupportedException>(() => agent.RunRoundAsync(
                new Round(Guid.NewGuid(), "nexus", "https://github.com/NaniSoft/nexus", 42, "main", string.Empty),
                CancellationToken.None));

            Assert.Contains("container runtime", round.Message, StringComparison.Ordinal);
        }
    }
}
