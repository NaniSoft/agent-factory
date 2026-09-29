namespace AgentFactory.Tests.Boundary;

using System.Net;
using AgentFactory;
using AgentFactory.Containers;
using AgentFactory.Failures;
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
    public async Task The_github_seam_is_a_real_client_and_a_call_it_cannot_make_says_which_one()
    {
        // **This test changed deliberately and visibly when the real client landed.** It
        // used to assert that the GitHub seam refused every call with a
        // `NotSupportedException` naming the merging ticket — the guard against a seam with
        // no implementation quietly doing nothing while the factory looks like it is
        // running. That guard was real and it did its job, and the thing it guarded is now
        // built, so keeping it would have meant keeping a refusal nobody wants.
        //
        // What replaces it is the same shape aimed at the failure it was for. The
        // registration is a real client rather than a refusal; a call that *can* be made
        // goes to GitHub; and a call that cannot — here, one against a project whose
        // credential this process's environment does not have — is refused before anything
        // leaves the building, with the credential's *name* in the message and a class on
        // the exception. A client that quietly returned an empty issue list would fail
        // this, because what is asserted is a refusal and not an absence.
        //
        // The endpoint is the real one and no token goes anywhere, because the refusal
        // happens first. A test that reached the network would be a test that depends on
        // this machine having a credential, and the point of this layer is that it is
        // hermetic.
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
            var github = app.Services.GetRequiredService<IGitHub>();
            Assert.IsType<GitHubClient>(github);

            var refusal = await Assert.ThrowsAsync<PermanentFailure>(
                () => github.ListOpenIssuesAsync("https://github.com/NaniSoft/nexus", CancellationToken.None));

            Assert.Contains("NEXUS_GITHUB_TOKEN", refusal.Message, StringComparison.Ordinal);
            Assert.Equal(FailureClass.Permanent, Failures.Classify(refusal));
        }
    }

    [Fact]
    public void The_agent_seam_is_a_real_container_runtime_and_not_a_refusal()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        using var app = FactoryApp.Create(
            WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(FactoryApp).Assembly.GetName().Name,
                ContentRootPath = root.Path,
                EnvironmentName = Environments.Development,
            }),
            new FactoryOptions(root.FactoriesDirectory, root.DatabasePath, new Uri("http://127.0.0.1:0")));

        // The counterpart to the seam test above, and the reason both seams are now of this
        // shape: a round asked for in a factory that still registered a refusal would say
        // the container runtime is not built, and a merge would say the client is not. Both
        // are built, and which runtime is a composition root's business rather than a
        // loop's.
        var agent = app.Services.GetRequiredService<INOpenCode>();

        Assert.IsType<WorkerRoundRunner>(agent);
        Assert.IsType<DockerCli>(app.Services.GetRequiredService<IDockerCli>());
        Assert.IsType<GitHubClient>(app.Services.GetRequiredService<IGitHub>());
    }

    [Fact]
    public void The_process_holds_exactly_one_http_client_and_the_merger_is_it()
    {
        // The application grew its first `HttpClient` when the GitHub client landed, and
        // three tests already depended on there not being one: the review surface's "the
        // diff renders without depending on GitHub being reachable" is checked partly by
        // asserting the diff reader's constructor takes no `HttpClient`, and that check is
        // only worth anything while a `HttpClient` is something a component *could* have.
        // None of those three needed changing, and that is the point of naming this one:
        // the claim is now stated rather than implied, and a second component reaching for
        // the network is a failure of this test rather than something a reader has to
        // notice by looking at constructors.
        //
        // It is a one-element list on purpose. The merger is the only component that talks
        // to GitHub, the board is the only surface, and this is the one place that
        // establishes the factory has exactly one way out to the network — which is the
        // structural form of "one GitHub seam" (ADR-0003, one process, one boundary).
        var holding = typeof(FactoryApp).Assembly
            .GetTypes()
            .Where(type => type.GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Any(parameter => parameter.ParameterType.Name is "HttpClient" or "IHttpClientFactory"))
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["GitHubClient"], holding);
    }
}
