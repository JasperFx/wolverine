using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Wolverine.Logging;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;

namespace Wolverine.CosmosDb.Internals.Durability;

public partial class CosmosDbDurabilityAgent : IAgent
{
    private readonly Container _container;
    private readonly IWolverineRuntime _runtime;
    private readonly CosmosDbMessageStore _parent;
    private readonly ILocalQueue _localQueue;
    private readonly DurabilitySettings _settings;
    private readonly ILogger<CosmosDbDurabilityAgent> _logger;

    private Task? _recoveryTask;
    private Task? _scheduledJob;

    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationTokenSource _combined;
    private PersistenceMetrics? _metrics;
    private readonly DurabilityHealthSignals _health;

    public CosmosDbDurabilityAgent(Container container, IWolverineRuntime runtime,
        CosmosDbMessageStore parent)
    {
        _container = container;
        _runtime = runtime;
        _parent = parent;
        _localQueue = (ILocalQueue)runtime.Endpoints.AgentForLocalQueue(TransportConstants.Scheduled);
        _settings = runtime.DurabilitySettings;

        Uri = new Uri("cosmosdb://durability");

        _logger = runtime.LoggerFactory.CreateLogger<CosmosDbDurabilityAgent>();

        _combined = CancellationTokenSource.CreateLinkedTokenSource(runtime.Cancellation, _cancellation.Token);
        _health = new DurabilityHealthSignals(_settings);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartTimers();
        return Task.CompletedTask;
    }

    internal void StartTimers()
    {
        _metrics = new PersistenceMetrics(_runtime, _settings, null);

        if (_settings.DurabilityMetricsEnabled)
        {
            _metrics.StartPolling(_runtime.LoggerFactory.CreateLogger<PersistenceMetrics>(), _parent);
        }

        var recoveryStart = _settings.ScheduledJobFirstExecution.Add(new Random().Next(0, 1000).Milliseconds());

        _recoveryTask = Task.Run(async () =>
        {
            await Task.Delay(recoveryStart, _combined.Token);
            using var timer = new PeriodicTimer(_settings.ScheduledJobPollingTime);

            while (!_combined.IsCancellationRequested)
            {
                var lastExpiredTime = DateTimeOffset.UtcNow;

                try
                {
                    await tryRecoverIncomingMessages();
                    await tryRecoverOutgoingMessagesAsync();

                    if (_settings.DeadLetterQueueExpirationEnabled)
                    {
                        var now = DateTimeOffset.UtcNow;
                        if (now > lastExpiredTime.AddHours(1))
                        {
                            await tryDeleteExpiredDeadLetters();
                        }
                    }

                    _health.RecordPollSuccess();
                }
                catch (Exception e) when (!_combined.IsCancellationRequested)
                {
                    _health.RecordPollFailure(e);
                    _logger.LogError(e, "Recovery loop tick failed");
                }

                await timer.WaitForNextTickAsync(_combined.Token);
            }
        }, _combined.Token);

        _scheduledJob = Task.Run(async () =>
        {
            await Task.Delay(recoveryStart, _combined.Token);
            using var timer = new PeriodicTimer(_settings.ScheduledJobPollingTime);

            while (!_combined.IsCancellationRequested)
            {
                try
                {
                    await runScheduledJobs();
                    _health.RecordPollSuccess();
                }
                catch (Exception e) when (!_combined.IsCancellationRequested)
                {
                    _health.RecordPollFailure(e);
                    _logger.LogError(e, "Scheduled-job loop tick failed");
                }

                await timer.WaitForNextTickAsync(_combined.Token);
            }
        }, _combined.Token);
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        PersistedCounts? counts = null;
        if (Status == AgentStatus.Running)
        {
            try
            {
                counts = await _parent.Admin.FetchCountsAsync();
            }
            catch (Exception e)
            {
                _health.RecordPollFailure(e);
            }
        }

        return _health.Evaluate(Status, Uri, counts, DateTimeOffset.UtcNow);
    }

    private async Task tryDeleteExpiredDeadLetters()
    {
        var now = DateTimeOffset.UtcNow;
        var queryText =
            "SELECT c.id, c.partitionKey FROM c WHERE c.docType = @docType AND c.expirationTime < @now";
        var query = new QueryDefinition(queryText)
            .WithParameter("@docType", DocumentTypes.DeadLetter)
            .WithParameter("@now", now);

        using var iterator = _container.GetItemQueryIterator<dynamic>(query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(DocumentTypes.DeadLetterPartition)
            });

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            foreach (var item in response)
            {
                string id = item.id;
                try
                {
                    await _container.DeleteItemAsync<dynamic>(id,
                        new PartitionKey(DocumentTypes.DeadLetterPartition));
                }
                catch (CosmosException)
                {
                    // Best effort
                }
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cancellation.Cancel();

        if (_metrics != null)
        {
            _metrics.SafeDispose();
        }

        if (_recoveryTask != null)
        {
            _recoveryTask.SafeDispose();
        }

        if (_scheduledJob != null)
        {
            _scheduledJob.SafeDispose();
        }

        return Task.CompletedTask;
    }

    public Uri Uri { get; set; }
    public AgentStatus Status { get; set; }

    /// <summary>
    /// True once <see cref="StartTimers"/> has wired up the recovery and scheduled-job
    /// background loops. Exposed for diagnostic and test inspection so callers can detect
    /// the multi-instance "two pollers" condition without reflection. See #2623.
    /// </summary>
    public bool IsPolling => _recoveryTask is not null || _scheduledJob is not null;
}
