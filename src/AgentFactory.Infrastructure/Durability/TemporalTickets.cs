namespace AgentFactory.Infrastructure.Durability;

using AgentFactory.Application;
using AgentFactory.Application.Durability;
using AgentFactory.Application.Tickets;
using AgentFactory.Application.WorkItems;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Temporalio.Client;
using Temporalio.Exceptions;
using Temporalio.Worker;

/// <summary>
/// The connection to durable execution, implemented: a real Temporal endpoint, or the
/// no-op that stands in for one in store-only mode.
/// </summary>
/// <remarks>
/// The worker's activity concurrency is the round concurrency — the same
/// <see cref="FactoryConstants.RoundConcurrency"/> the loop counted — because at most that
/// many round activities may run at once across every ticket, which is what the
/// concurrency always meant in store-only mode: a bound on rounds, not on tickets.
/// </remarks>
public sealed class TemporalTickets : BackgroundService, ITicketStarter
{
    /// <summary>The task queue the ticket workflows and their activities live on.</summary>
    public const string TaskQueue = "agent-factory-tickets";

    private readonly IServiceProvider _services;
    private readonly FactoryOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TemporalTickets> _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private ITemporalClient? _client;

    public TemporalTickets(
        IServiceProvider services,
        FactoryOptions options,
        ILoggerFactory loggerFactory)
    {
        _services = services;
        _options = options;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<TemporalTickets>();
    }

    public bool Configured => !string.IsNullOrWhiteSpace(_options.TemporalEndpoint);

    /// <summary>
    /// Hosts the worker: workflows and activities for every ticket, until the process
    /// stops. Reconnecting is the loop around the connect, and it is what makes a Temporal
    /// outage a degraded board rather than a dead factory — the worker picks the flow back
    /// up when the server returns, with every ticket exactly where its history says.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ITemporalClient client;
            try
            {
                client = await TemporalClient.ConnectAsync(new()
                {
                    TargetHost = _options.TemporalEndpoint,
                    Namespace = "default",
                }).ConfigureAwait(false);
            }
            catch (Exception refused)
            {
                _logger.LogWarning(
                    "Temporal at {Endpoint} is unreachable: {Refusal}. The board serves and intake still records; "
                        + "the worker reconnects in ten seconds.",
                    _options.TemporalEndpoint,
                    refused.Message);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                continue;
            }

            _client = client;

            var activities = new TicketActivities(
                _services.GetRequiredService<IWorkItemStore>(),
                _services.GetRequiredService<TicketPolicy>(),
                _loggerFactory.CreateLogger<TicketActivities>());

            using var worker = new TemporalWorker(
                client,
                new TemporalWorkerOptions(TaskQueue)
                {
                    // The round concurrency: at most this many round activities run at once
                    // across every ticket, which is the bound the loop counted in store-only
                    // mode and the same number carried by the engine here.
                    MaxConcurrentActivities = FactoryConstants.RoundConcurrency,
                }
                .AddWorkflow<TicketWorkflow>()
                .AddAllActivities<ITicketActivities>(activities));

            try
            {
                _logger.LogInformation(
                    "The ticket worker is connected to Temporal at {Endpoint}.",
                    _options.TemporalEndpoint);
                await worker.ExecuteAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception refused)
            {
                _logger.LogWarning(
                    "The ticket worker stopped: {Refusal}. Reconnecting in ten seconds.",
                    refused.Message);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task StartTicketAsync(Guid workItemId)
    {
        var client = await ClientAsync().ConfigureAwait(false);
        try
        {
            await client.StartWorkflowAsync(
                (TicketWorkflow wf) => wf.RunAsync(new TicketInput(
                    workItemId,
                    AutoMerge: _options.AutoMerge)),
                new(id: $"ticket-{workItemId:N}", taskQueue: TaskQueue)).ConfigureAwait(false);

            _logger.LogInformation(
                "A ticket workflow started for {WorkItem}. The story from here is durable.",
                workItemId);
        }
        catch (WorkflowAlreadyStartedException)
        {
            // Self-healing, not an error: intake re-offers every known issue each pass, so
            // an already-running workflow is the ordinary answer for a ticket that has one.
        }
    }

    /// <inheritdoc />
    public async Task<(bool Handled, bool Applied, string? Refusal)> AcceptAsync(Guid workItemId)
    {
        var client = await ClientAsync().ConfigureAwait(false);
        var handle = client.GetWorkflowHandle<TicketWorkflow>($"ticket-{workItemId:N}");
        try
        {
            await handle.ExecuteUpdateAsync(wf => wf.Accept()).ConfigureAwait(false);
            return (true, true, null);
        }
        catch (RpcException refused) when (refused.Code.ToString().Contains("NotFound", StringComparison.OrdinalIgnoreCase))
        {
            return (false, false, null);
        }
    }

    /// <inheritdoc />
    public async Task<(bool Handled, bool Applied, string? Refusal)> DecideAsync(
        Guid workItemId, string decision, string? feedback)
    {
        var client = await ClientAsync().ConfigureAwait(false);
        var handle = client.GetWorkflowHandle<TicketWorkflow>($"ticket-{workItemId:N}");
        try
        {
            var answer = await handle.ExecuteUpdateAsync(
                wf => wf.DecideAsync(new TicketDecision(decision, feedback))).ConfigureAwait(false);
            return (true, answer.Applied, answer.Refusal);
        }
        catch (RpcException refused) when (refused.Code.ToString().Contains("NotFound", StringComparison.OrdinalIgnoreCase))
        {
            return (false, false, null);
        }
    }

    private async Task<ITemporalClient> ClientAsync()
    {
        if (_client is { } connected)
        {
            return connected;
        }

        await _connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client is not { } existing)
            {
                _client = await TemporalClient.ConnectAsync(new()
                {
                    TargetHost = _options.TemporalEndpoint,
                    Namespace = "default",
                }).ConfigureAwait(false);
            }

            return _client;
        }
        finally
        {
            _connectLock.Release();
        }
    }
}

/// <summary>The starter that is not there: store-only mode. Every call is a no-op.</summary>
public sealed class NullTicketStarter : ITicketStarter
{
    public bool Configured => false;

    public Task StartTicketAsync(Guid workItemId) => Task.CompletedTask;

    public Task<(bool Handled, bool Applied, string? Refusal)> AcceptAsync(Guid workItemId) =>
        Task.FromResult((false, false, (string?)null));

    public Task<(bool Handled, bool Applied, string? Refusal)> DecideAsync(
        Guid workItemId, string decision, string? feedback) =>
        Task.FromResult((false, false, (string?)null));
}
