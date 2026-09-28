namespace AgentFactory.Tests.Boundary;

using AgentFactory;
using AgentFactory.Clock;
using AgentFactory.Projects;
using AgentFactory.WorkItems;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// The application boundary: the real factory, started in-process. Config loading,
/// the store and the board are the real thing; only the three fakes are substituted.
/// Nothing here is a stand-in for the factory itself.
/// </summary>
public sealed class FactoryHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FactoryHost(WebApplication app, HttpClient board, TestClock clock)
    {
        _app = app;
        Board = board;
        Clock = clock;
    }

    /// <summary>HTTP client for the board, over a real socket and a real listener.</summary>
    public HttpClient Board { get; }

    /// <summary>The fake clock the running factory is wired to.</summary>
    public TestClock Clock { get; }

    /// <summary>The real store the running factory is wired to.</summary>
    public IWorkItemStore Store => _app.Services.GetRequiredService<IWorkItemStore>();

    /// <summary>What the config loader served at start, and what it refused.</summary>
    public ProjectLoadReport Projects => _app.Services.GetRequiredService<ProjectLoadReport>();

    /// <summary>The address the board is actually listening on.</summary>
    public string BoardAddress { get; private set; } = string.Empty;

    public static Task<FactoryHost> StartAsync(FactoryRoot root, TestClock? clock = null) =>
        StartAsync(root, new FactoryOptions(root.FactoriesDirectory, root.DatabasePath, new Uri("http://127.0.0.1:0")), clock);

    public static Task<FactoryHost> StartAsync(FactoryRoot root, Uri boardUrl, TestClock? clock = null) =>
        StartAsync(root, new FactoryOptions(root.FactoriesDirectory, root.DatabasePath, boardUrl), clock);

    /// <summary>Starts the factory the way its own entry point does: options from configuration.</summary>
    public static async Task<FactoryHost> StartAsync(FactoryRoot root, FactoryOptions options, TestClock? clock = null)
    {
        var testClock = clock ?? new TestClock();

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

        var app = FactoryApp.Create(builder, options);
        await app.StartAsync();

        var address = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .First();

        return new FactoryHost(app, new HttpClient { BaseAddress = new Uri(address) }, testClock)
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
