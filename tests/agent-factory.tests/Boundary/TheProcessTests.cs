namespace AgentFactory.Tests.Boundary;

using AgentFactory.Application;
using AgentFactory;
using System.Net;

using AgentFactory.Domain.Failures;
using AgentFactory.Application.Rounds;
using AgentFactory.Infrastructure.Rounds;
using AgentFactory.Application.GitHub;
using AgentFactory.Infrastructure.GitHub;
using AgentFactory.Application.Loop;
using AgentFactory.Application.Polling;
using AgentFactory.Application.WorkItems;
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
/// <remarks>
/// In the same collection as <c>CredentialFreeObservabilityTests</c>, and for one reason:
/// that class sets a process-wide environment variable to a real token and clears it again,
/// and xUnit runs classes in parallel. Left alone, the seam test below would sometimes
/// find <c>NEXUS_GITHUB_TOKEN</c> set by its neighbour, go to github.com instead of being
/// refused locally, and fail on a 403 — which is a green suite failing for a reason that
/// has nothing to do with the code under test.
/// </remarks>
[Collection("the process environment")]
public class TheProcessTests
{
    [Fact]
    public async Task The_board_answers_from_the_one_process()
    {
        using var root = FactoryRoot.Create();
        await using var host = await FactoryHost.StartAsync(root);

        var response = await host.Board.GetAsync("/api/board");

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
            using var response = await client.GetAsync("/api/board");

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
    public void The_agent_seam_is_a_real_round_runner_and_not_a_refusal()
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
        // the agent is not built, and a merge would say the client is not. Both are built,
        // and which runner is a composition root's business rather than a loop's. The
        // process seam is named too, because the runner is the one thing that starts
        // programs and the seam is what a test fakes to drive it.
        var agent = app.Services.GetRequiredService<INOpenCode>();

        Assert.IsType<RoundRunner>(agent);
        Assert.IsType<ProcessCli>(app.Services.GetRequiredService<IProcessCli>());
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
        // structural form of "one GitHub seam".
        //
        // The scan is over every assembly the factory is made of rather than over the entry
        // assembly, and that is a correction rather than a widening: `GitHubClient` moved to
        // `Infrastructure` in this restructure and this scan went on reading `Web`, where it
        // found nothing and would have reported "no component holds a transport" — the exact
        // inversion of what it exists to say.
        //
        // **#63 gave the process a second way out to the network and this claim is
        // deliberately unchanged.** The OTLP exporter connects out to the collector, and its
        // `HttpClient` is the OpenTelemetry SDK's own, inside that package's assembly. The
        // claim here is about *components* — a type in one of these four assemblies whose
        // constructor takes a transport — and adding one is still a failing test.
        var holding = TheFactory.Assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Any(parameter => parameter.ParameterType.Name is "HttpClient" or "IHttpClientFactory"))
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["GitHubClient"], holding);
    }

    [Fact]
    public void The_github_clients_pipeline_carries_no_second_retry_policy()
    {
        // **The pin for the most dangerous thing in #63, and it is about the pipeline that
        // actually exists rather than about which line somebody remembered not to write.**
        //
        // Aspire's `AddServiceDefaults()` installs the standard resilience handler on *every*
        // named and typed `HttpClient` through `ConfigureHttpClientDefaults`, and the GitHub
        // client is a named one — so adopting the template wholesale would put Polly between
        // the merger and the wire. Three failures follow, and the first is the one this
        // repository cannot tolerate: its attempt timeout is 10 seconds, it signals by throwing
        // `TimeoutRejectedException`, and `GitHubClient.AskAsync` catches
        // `OperationCanceledException`/`TimeoutException` and `HttpRequestException` — neither
        // of which is that. The exception would therefore leave the seam unclassified, which is
        // precisely the failure `GitHubResponse` and `Failures.Classify` exist to prevent:
        // `GitHubResponse.Unclassifiable` already names "an answer this client cannot read as
        // the API's shape" as a thing to be decided rather than guessed at, and an unclassified
        // escape is worse than a wrong answer. The other two are retries on non-idempotent
        // calls (create, merge, close) and a second retry policy stacked on the deliberate one
        // in `Orchestrator.Approve`.
        //
        // The prevention is structural in two places, and this is the one that reads the
        // running process. The package is not a `PackageReference` of any project —
        // `ArchitectureTests.No_project_takes_the_http_resilience_package` is that half — so the
        // line is not something a later edit can reach for by accident. This is the other half:
        // resolve the handler chain the factory really builds for `github` and assert that
        // nothing in it comes from the resilience package or from Polly, and that the bound on
        // a request is still the factory's own rather than a handler's attempt timeout. It
        // fires if the handler is installed by any route at all, including one this repository
        // did not write. It was proved to fire by adding the handler and watching it fail.
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        using var app = FactoryApp.Create(
            WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(FactoryApp).Assembly.GetName().Name,
                ContentRootPath = root.Path,
                EnvironmentName = Environments.Development,
            }),
            new FactoryOptions(root.FactoriesDirectory, root.DatabasePath, new Uri("http://127.0.0.1:0")));

        var pipeline = app.Services
            .GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(GitHubClient.HttpClientName);

        var handlers = new List<Type>();
        for (var handler = pipeline; handler is not null; handler = (handler as DelegatingHandler)?.InnerHandler)
        {
            handlers.Add(handler.GetType());
        }

        // The positive control, and the reason the negative assertions below are not vacuous:
        // a reader that walked nothing at all would satisfy them. The chain is real — it is
        // several handlers — and it ends in an actual transport.
        Assert.True(handlers.Count > 1, "the github client's handler chain was empty: the walk read nothing");
        Assert.Contains(handlers, type => type == typeof(SocketsHttpHandler) || type == typeof(HttpClientHandler));

        // And nothing from the two packages that would mean a second retry policy. Checked by
        // assembly rather than by type name, because the handler itself is internal to the
        // resilience package and a name-based check would be a check on something a patch
        // release could rename.
        Assert.DoesNotContain(
            handlers,
            type => type.Assembly.GetName().Name is { } assembly
                && (assembly.StartsWith("Microsoft.Extensions.Http.Resilience", StringComparison.Ordinal)
                    || assembly.StartsWith("Polly", StringComparison.Ordinal)));

        // The bound on a call is the factory's own, and it is not the 10 seconds the standard
        // handler's attempt timeout would impose. `GitHubClient.RequestTimeout` is the one place
        // that number lives, and `PolicyTests.The_merger_is_the_first_component_with_a_timeout_
        // and_it_bought_none` is where the claim that it exists at all is made.
        Assert.Equal(
            GitHubClient.RequestTimeout,
            app.Services.GetRequiredService<IHttpClientFactory>().CreateClient(GitHubClient.HttpClientName).Timeout);
    }
}
