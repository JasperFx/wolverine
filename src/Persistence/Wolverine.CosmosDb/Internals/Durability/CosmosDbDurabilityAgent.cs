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

        Uri = new Uri($"{PersistenceConstants.AgentScheme}://cosmosdb/durability");

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

            // GH-4286: this throttle lives OUTSIDE the loop — reset per iteration, the hourly guard
            // below could only fire if a single recovery tick took more than an hour, so expired dead
            // letters were never deleted. MinValue makes the first tick sweep immediately, matching the
            // RDBMS providers' expiration timer that first fires a minute after startup.
            var lastExpiredTime = DateTimeOffset.MinValue;

            // GH-4509: same shape, its own cadence. MinValue makes the first tick sweep immediately.
            var lastHandledCleanup = DateTimeOffset.MinValue;

            while (!_combined.IsCancellationRequested)
            {
                try
                {
                    await tryRecoverIncomingMessages();
                    await tryRecoverOutgoingMessagesAsync();

                    // GH-4509: handled inbox documents were never deleted on this provider -- keepUntil was
                    // stamped and nothing read it back, so they accumulated forever, bodies included.
                    var handledCleanupAt = DateTimeOffset.UtcNow;
                    if (handledCleanupAt > lastHandledCleanup.Add(_settings.HandledMessageCleanupPollingTime))
                    {
                        await tryDeleteExpiredHandledEnvelopes();
                        lastHandledCleanup = handledCleanupAt;
                    }

                    if (_settings.DeadLetterQueueExpirationEnabled)
                    {
                        // Crudely just doing this every hour
                        var now = DateTimeOffset.UtcNow;
                        if (now > lastExpiredTime.AddHours(1))
                        {
                            await tryDeleteExpiredDeadLetters();
                            lastExpiredTime = now;
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

    /// <summary>
    /// GH-4509. Delete inbox documents whose <c>keepUntil</c> has passed. Without this,
    /// <c>MarkIncomingEnvelopeAsHandledAsync</c> stamped a <c>keepUntil</c> that nothing ever read back:
    /// there was no equivalent of the RDBMS <c>DeleteExpiredHandledEnvelopesCommand</c>, no <c>ttl</c> is
    /// written, and container TTL is off, so handled inbox documents were kept forever.
    /// </summary>
    /// <remarks>
    /// <para>This bites harder on Cosmos than on a SQL store. <c>IncomingMessage.PartitionKey</c> is the
    /// envelope's destination, so every handled envelope for one listening endpoint piles into a single
    /// logical partition — a 20 GB hard ceiling and a 10k RU/s cap. <c>FetchCountsAsync</c>, which
    /// <c>CheckHealthAsync</c> calls on every health check, counts that pile cross-partition too.</para>
    ///
    /// <para>Unlike <see cref="tryDeleteExpiredDeadLetters" />, this query is necessarily cross-partition:
    /// dead letters all share one partition key, handled inbox documents do not. Each delete therefore has
    /// to carry the document's own partition key, which is why the projection selects it.</para>
    ///
    /// <para>Bounded by <see cref="DurabilitySettings.HandledMessageCleanupBatchSize" /> and
    /// <see cref="DurabilitySettings.HandledMessageCleanupMaxBatchesPerCycle" />. Those knobs read as
    /// general durability settings but were referenced only from <c>Wolverine.RDBMS.DurabilityAgent</c>, so
    /// on Cosmos they did nothing at all before this.</para>
    /// </remarks>
    private async Task tryDeleteExpiredHandledEnvelopes()
    {
        // Compared as TIMESTAMPS, not as strings. keepUntil is persisted as an ISO-8601 string that keeps
        // whatever offset it was written with -- documents in this container carry "-05:00" -- and Cosmos
        // compares two strings lexicographically, so `c.keepUntil < @now` against a UTC-rendered parameter
        // is meaningless the moment the two offsets differ. It deleted a document half an hour short of its
        // expiry in test. DateTimeToTimestamp normalises both sides to epoch milliseconds, and it reads the
        // offset, so it is also correct for documents already written by an older version.
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var query = new QueryDefinition(
                "SELECT c.id, c.partitionKey FROM c WHERE c.docType = @docType AND c.status = @status " +
                "AND DateTimeToTimestamp(c.keepUntil) < @now")
            .WithParameter("@docType", DocumentTypes.Incoming)
            .WithParameter("@status", EnvelopeStatus.Handled)
            .WithParameter("@now", now);

        using var iterator = _container.GetItemQueryIterator<dynamic>(query,
            requestOptions: new QueryRequestOptions
            {
                MaxItemCount = _settings.HandledMessageCleanupBatchSize
            });

        var batches = 0;
        while (iterator.HasMoreResults && batches < _settings.HandledMessageCleanupMaxBatchesPerCycle
                                       && !_combined.IsCancellationRequested)
        {
            var response = await iterator.ReadNextAsync(_combined.Token);
            batches++;

            foreach (var item in response)
            {
                string id = item.id;
                string partitionKey = item.partitionKey;

                try
                {
                    await _container.DeleteItemAsync<dynamic>(id, new PartitionKey(partitionKey),
                        cancellationToken: _combined.Token);
                }
                catch (CosmosException)
                {
                    // Best effort, the same as the dead letter sweep: another node may have taken it, and
                    // the next cycle picks up anything this one missed
                }
            }
        }
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

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cancellation.CancelAsync();

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
    }

    public Uri Uri { get; set; }
    public AgentStatus Status { get; set; }

    /// <summary>
    /// Human-readable description for monitoring tools — see
    /// <see cref="IAgent.Description"/>.
    /// </summary>
    public string Description => $"Wolverine Cosmos DB durability agent for {Uri} — recovers persisted inbox/outbox messages and runs scheduled jobs against the Cosmos DB message store.";

    /// <summary>
    /// True once <see cref="StartTimers"/> has wired up the recovery and scheduled-job
    /// background loops. Exposed for diagnostic and test inspection so callers can detect
    /// the multi-instance "two pollers" condition without reflection. See #2623.
    /// </summary>
    public bool IsPolling => _recoveryTask is not null || _scheduledJob is not null;
}
