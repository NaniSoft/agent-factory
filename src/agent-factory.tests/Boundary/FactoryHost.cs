namespace AgentFactory.Tests.Boundary;

using System.Reflection;
using AgentFactory;
using AgentFactory.Clock;
using AgentFactory.Driving;
using AgentFactory.GitHub;
using AgentFactory.Loop;
using AgentFactory.Polling;
using AgentFactory.Pages;
using AgentFactory.Projects;
using AgentFactory.Rounds;
using AgentFactory.WorkItems;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// The application boundary: the real factory, started in-process. Config loading, the
/// store, the orchestrator and the board are the real thing; only the seams are
/// substituted. Nothing here is a stand-in for the factory itself.
/// </summary>
public sealed class FactoryHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FactoryHost(WebApplication app, HttpClient board, TestClock clock, FakeNOpenCode agent, FakeGitHub github)
    {
        _app = app;
        Board = board;
        Clock = clock;
        Agent = agent;
        GitHub = github;
    }

    /// <summary>HTTP client for the board, over a real socket and a real listener.</summary>
    public HttpClient Board { get; }

    /// <summary>The fake clock the running factory is wired to.</summary>
    public TestClock Clock { get; }

    /// <summary>
    /// The fake agent the running factory is wired to. Script it before starting the
    /// factory; the loop asks it for one round per round it runs.
    /// </summary>
    public FakeNOpenCode Agent { get; }

    /// <summary>
    /// The fake GitHub the running factory is wired to. Intake reads every repository's
    /// open issues and default branch through this one seam; script a repository before
    /// starting the factory, and read the turns back off it afterwards. The loop merges
    /// through the same seam, and refuses to merge until a test asks for merges that land
    /// — so an approval cannot quietly succeed against a factory that has no merger.
    /// </summary>
    public FakeGitHub GitHub { get; }

    /// <summary>
    /// The real orchestrator, in the running process. A test starts the real factory,
    /// scripts the seams it needs, and steps the real state machine rather than waiting on
    /// a background timer, so no test sleeps or polls. Stepping it directly is what makes
    /// the 90-minute round timeout testable: time moves on the fake clock and the machine
    /// is asked again.
    /// </summary>
    public async Task<bool> Step() => (await _app.Services.GetRequiredService<Orchestrator>().StepAsync()).Applied;

    /// <summary>Applies transitions until the machine has nothing left to apply.</summary>
    public Task Settle() => _app.Services.GetRequiredService<Orchestrator>().SettleAsync();

    /// <summary>
    /// The real heartbeat, in the running process. A test calls <see cref="Tick"/> to move
    /// the machine the way production does — one tick, no sleep — rather than reaching for
    /// the orchestrator directly, so what is under test is the thing that will actually be
    /// driving the factory.
    /// </summary>
    public Task Tick() => _app.Services.GetRequiredService<FactoryDriver>().TickAsync();

    /// <summary>
    /// How many worker containers the factory is inside right now, read the same way the
    /// board reads it.
    /// </summary>
    public int RoundsInFlight => _app.Services.GetRequiredService<Orchestrator>().RoundsInFlight;

    /// <summary>
    /// Settles the machine, and says so rather than hanging if it takes unreasonably long.
    /// </summary>
    /// <remarks>
    /// The limit is a guard against a loop that is slow rather than unbounded, and it is
    /// worth being precise about what it cannot do: a <em>tight</em> loop inside
    /// <see cref="SettleAsync"/> completes synchronously, so the continuation that would
    /// observe the timeout is never reached and the wait cannot fire. A retry policy with
    /// no ceiling at all has to be caught some other way, and the tests that assert
    /// "nothing more happens" do that by taking a bounded number of
    /// <see cref="Step"/>s rather than settling. This is here so that a machine which is
    /// still working after thirty seconds says so rather than looking like a hang.
    /// </remarks>
    public async Task SettleWithin(TimeSpan limit)
    {
        try
        {
            await Settle().WaitAsync(limit);
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException(
                $"the machine was still applying transitions after {limit.TotalSeconds:0}s: something is retrying "
                    + "without a ceiling, or a transition is being applied over and over");
        }
    }

    /// <summary>
    /// The real poller, in the running process. One step is one project's turn at
    /// intake, taken only when the poll interval has passed — so a test drives intake by
    /// advancing the clock and stepping, never by waiting.
    /// </summary>
    public Task<bool> IntakeStepAsync() => _app.Services.GetRequiredService<Poller>().StepAsync();

    /// <summary>A whole pass: every project's turn, in rotation order.</summary>
    public Task PollAsync() => _app.Services.GetRequiredService<Poller>().PassAsync();

    /// <summary>The real store the running factory is wired to.</summary>
    public IWorkItemStore Store => _app.Services.GetRequiredService<IWorkItemStore>();

    /// <summary>What the config loader served at start, and what it refused.</summary>
    public ProjectLoadReport Projects => _app.Services.GetRequiredService<ProjectLoadReport>();

    /// <summary>
    /// Every route the running process serves. Anything a request — and so anything a
    /// human — can reach has to arrive through one of these, so the list is the whole of
    /// what is addressable.
    /// </summary>
    public IReadOnlyList<string> Routes() => _app.Services
        .GetRequiredService<EndpointDataSource>()
        .Endpoints
        .Select(endpoint => (endpoint as RouteEndpoint)?.RoutePattern.RawText is { Length: > 0 } raw
            ? raw
            : endpoint.DisplayName ?? "?")
        .Distinct()
        .OrderBy(route => route, StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// The board's handlers, by name. A Razor page is dispatched inside one endpoint, so
    /// the handlers are where a page's read path and its write path can be told apart.
    /// </summary>
    public IReadOnlyList<string> BoardHandlers() => typeof(IndexModel)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(method => method.Name.StartsWith("On", StringComparison.Ordinal))
        .Select(method => method.Name)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToList();

    /// <summary>The address the board is actually listening on.</summary>
    public string BoardAddress { get; private set; } = string.Empty;

    public static Task<FactoryHost> StartAsync(
        FactoryRoot root,
        TestClock? clock = null,
        FakeNOpenCode? agent = null,
        FakeGitHub? github = null) =>
        StartAsync(root, new FactoryOptions(root.FactoriesDirectory, root.DatabasePath, new Uri("http://127.0.0.1:0")), clock, agent, github);

    public static Task<FactoryHost> StartAsync(
        FactoryRoot root,
        Uri boardUrl,
        TestClock? clock = null,
        FakeNOpenCode? agent = null,
        FakeGitHub? github = null) =>
        StartAsync(root, new FactoryOptions(root.FactoriesDirectory, root.DatabasePath, boardUrl), clock, agent, github);

    /// <summary>Starts the factory the way its own entry point does: options from configuration.</summary>
    public static async Task<FactoryHost> StartAsync(
        FactoryRoot root,
        FactoryOptions options,
        TestClock? clock = null,
        FakeNOpenCode? agent = null,
        FakeGitHub? github = null)
    {
        var testClock = clock ?? new TestClock();
        var fakeAgent = agent ?? new FakeNOpenCode();
        var fakeGitHub = github ?? new FakeGitHub();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // The factory is a library to the test host, so MVC has to be told which
            // assembly the board's compiled pages live in.
            ApplicationName = typeof(FactoryApp).Assembly.GetName().Name,
            ContentRootPath = root.Path,
            EnvironmentName = Environments.Development,
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton<IClock>(testClock);

        // The agent is the one seam with no production implementation yet, so it is
        // registered here, in the host that has one. The loop takes it as a dependency
        // and never learns what is behind it.
        builder.Services.AddSingleton<INOpenCode>(fakeAgent);

        // GitHub is the other such seam: one boundary covering both intake and merging,
        // and no implementation behind it yet either. The poller takes it as a
        // dependency and never learns what is behind it.
        builder.Services.AddSingleton<IGitHub>(fakeGitHub);

        var app = FactoryApp.Create(builder, options, drivingTheMachine: false);
        await app.StartAsync();

        var address = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .First();

        return new FactoryHost(app, new HttpClient { BaseAddress = new Uri(address) }, testClock, fakeAgent, fakeGitHub)
        {
            BoardAddress = address,
        };
    }

    public async ValueTask DisposeAsync()
    {
        Board.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
