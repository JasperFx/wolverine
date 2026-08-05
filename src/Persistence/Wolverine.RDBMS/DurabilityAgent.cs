using JasperFx;
using JasperFx.Blocks;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.Logging;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS.Durability;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;

namespace Wolverine.RDBMS;

internal class DurabilityAgent : IAgent
{
    private readonly IMessageDatabase _database;
    private readonly ILogger<DurabilityAgent> _logger;
    private readonly Block<IAgentCommand> _runningBlock;

    private readonly IWolverineRuntime _runtime;
    private readonly DurabilitySettings _settings;
    private Timer? _expirationTimer;
    private PersistenceMetrics _metrics = null!;
    private IDisposable? _metricsRegistration;
    private Timer? _recoveryTimer;
    private Timer? _scheduledJobTimer;
    private Timer? _handledCleanupTimer;
    private Timer? _nodeRecordPruningTimer;

    private readonly DurabilityHealthSignals _health;
    private DateTime _lastHealthCheck = DateTime.UtcNow;

    public DurabilityAgent(IWolverineRuntime runtime, IMessageDatabase database)
    {
        _runtime = runtime;
        _database = database;
        _settings = runtime.DurabilitySettings;

        Uri = database.Uri;

        var executor = runtime.As<IExecutorFactory>().BuildFor(typeof(IAgentCommand));

        _logger = runtime.LoggerFactory.CreateLogger<DurabilityAgent>();

        _health = new DurabilityHealthSignals(_settings);

#pragma warning disable VSTHRD101 // Avoid unsupported async delegates
        _runningBlock = new Block<IAgentCommand>(async batch =>
        {
            if (runtime.Cancellation.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await executor.InvokeAsync(batch, new MessageBus(runtime));
                _health.RecordPollSuccess();
            }
            catch (Exception e)
            {
                _health.RecordPollFailure(e);
                _logger.LogError(e, "Error trying to run durability agent commands");
            }
        });
#pragma warning restore VSTHRD101 // Avoid unsupported async delegates
    }

    public bool AutoStartScheduledJobPolling { get; set; } = false;

    public AgentStatus Status { get; set; } = AgentStatus.Running;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _metrics = new PersistenceMetrics(_runtime, _settings, _database.Name);

        if (_settings.DurabilityMetricsEnabled)
        {
            // GH-3375: register with the node's single sequential metrics sweeper instead of
            // running a per-database PeriodicTimer. At high database counts the in-phase
            // per-agent pollers each pinned a pooled connection; the sweeper walks the node's
            // databases one at a time across the UpdateMetricsPeriod window.
            _metricsRegistration = PersistenceMetricsSweeper.For(_runtime).Register(_database, _metrics);
        }

        var recoveryStart = _settings.ScheduledJobFirstExecution.Add(new Random().Next(0, 1000).Milliseconds());

#pragma warning disable VSTHRD101 // Avoid unsupported async delegates
        _recoveryTimer = new Timer(async _ =>
        {
            IReadOnlyList<int>? activeNodeNumbers = null;
            if (_settings.Mode != DurabilityMode.Solo && _database.Settings.Role != MessageStoreRole.Main)
            {
                try
                {
                    // Node-wide, not per-database: LoadAllNodesAsync also selects the whole assignment
                    // table to populate ActiveAgents, which this caller never reads, and there is one
                    // durability agent per message database. See ActiveNodeNumberCache.
                    activeNodeNumbers = await ActiveNodeNumberCache.For(_runtime)
                        .FetchAsync(_runtime.Cancellation);
                }
                catch (Exception e)
                {
                    _logger.LogDebug(e, "Failed to load active nodes for orphaned message detection");
                }
            }

            var operations = buildOperationBatch(activeNodeNumbers);

            var batch = new DatabaseOperationBatch(_database, operations);
            _runningBlock.Post(batch);
        }, _settings, recoveryStart, _settings.ScheduledJobPollingTime);
#pragma warning restore VSTHRD101 // Avoid unsupported async delegates

        if (_settings.DeadLetterQueueExpirationEnabled)
        {
            _expirationTimer = new Timer(_ =>
            {
                var operations = new IDatabaseOperation[]
                {
                    new DeleteExpiredDeadLetterMessagesOperation(_database, _logger, DateTimeOffset.UtcNow)
                };

                var batch = new DatabaseOperationBatch(_database, operations);
                _runningBlock.Post(batch);
            }, _settings, 1.Minutes(), 1.Hours());
        }

        _handledCleanupTimer = new Timer(_ =>
        {
            var command = new DeleteExpiredHandledEnvelopesCommand(_database, _settings, _logger);
            _runningBlock.Post(command);
        }, _settings, 5.Seconds(), _settings.HandledMessageCleanupPollingTime);

        // GH-3701: node records only exist on the Main store, and their housekeeping is slow, unbounded-scan
        // work that has no business riding along on the recovery batch. See PruneNodeRecords.
        if (_database.Settings.Role == MessageStoreRole.Main)
        {
            // Hold the first pass back so it can't pile onto startup, but never past the configured period
            // itself -- a host that asks for a short period is asking to see pruning promptly.
            var pruningStart = _settings.NodeRecordPruningPeriod < 1.Minutes()
                ? _settings.NodeRecordPruningPeriod
                : 1.Minutes();

            _nodeRecordPruningTimer = new Timer(_ => PruneNodeRecords(), _settings, pruningStart,
                _settings.NodeRecordPruningPeriod);
        }

        if (AutoStartScheduledJobPolling)
        {
            StartScheduledJobPolling();
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _runningBlock.Complete();
        _metricsRegistration?.Dispose();
        _metrics.SafeDispose();

        if (_scheduledJobTimer != null)
        {
            await _scheduledJobTimer.DisposeAsync();
        }

        if (_recoveryTimer != null)
        {
            await _recoveryTimer.DisposeAsync();
        }

        if (_expirationTimer != null)
        {
            await _expirationTimer.DisposeAsync();
        }

        if (_handledCleanupTimer != null)
        {
            await _handledCleanupTimer.DisposeAsync();
        }

        if (_nodeRecordPruningTimer != null)
        {
            await _nodeRecordPruningTimer.DisposeAsync();
        }

        Status = AgentStatus.Stopped;
    }

    public Uri Uri { get; internal set; }

    /// <summary>
    /// Human-readable description for monitoring tools - see
    /// <see cref="IAgent.Description"/>. This agent recovers
    /// persisted inbox / outbox messages from the relational
    /// message store, runs scheduled jobs, and emits persistence
    /// metrics. The URI carries the store identity so operators
    /// can disambiguate when a service has multiple stores.
    /// </summary>
    public string Description => $"Wolverine durability agent for {Uri} - recovers persisted inbox/outbox messages, runs scheduled jobs, and emits persistence metrics.";

    public static Uri SimplifyUri(Uri uri)
    {
        return new Uri($"{PersistenceConstants.AgentScheme}://{uri.Host}");
    }

    public static Uri AddMarkerType(Uri uri, Type markerType)
    {
        return new Uri($"{uri}{markerType.Name}");
    }

    /// <summary>
    /// GH-3701: everything that keeps the node record table from growing without bound. Two deletes, both
    /// against the <c>Main</c> store, both on the slow <see cref="DurabilitySettings.NodeRecordPruningPeriod"/>
    /// timer rather than the five-second recovery batch:
    ///
    /// 1. <see cref="DeleteOldNodeEventRecords"/> — the age sweep against
    ///    <see cref="DurabilitySettings.NodeEventRecordExpirationTime"/> (5 days by default). This one already
    ///    existed, but it was appended to <see cref="buildOperationBatch"/> behind an
    ///    <c>isTimeToPruneNodeEventRecords()</c> guard reading a field that was never assigned — the
    ///    suppression on it (<c>CS0649</c>) said so outright — so the guard always returned true and the whole
    ///    delete ran on every recovery cycle.
    /// 2. <see cref="TrimNodeRecordsCommand"/> — the row cap against
    ///    <see cref="DurabilitySettings.NodeRecordRetention"/>. An age bound alone puts no ceiling on the
    ///    table: the reporting cluster wrote one <c>AssignmentChanged</c> row per agent per assignment
    ///    decision, ~12.8M rows/day, reaching 36M rows / 16 GB well inside the 5-day window.
    /// </summary>
    internal void PruneNodeRecords()
    {
        _runningBlock.Post(new DatabaseOperationBatch(_database,
            [new DeleteOldNodeEventRecords(_database, _settings)]));

        if (_settings.NodeRecordRetention > 0)
        {
            _runningBlock.Post(new TrimNodeRecordsCommand(_database, _settings, _logger));
        }
    }

    internal IDatabaseOperation[] buildOperationBatch(IReadOnlyList<int>? activeNodeNumbers = null)
    {
        var incomingTable = new DbObjectName(_database.SchemaName, DatabaseConstants.IncomingTable);
        var now = DateTimeOffset.UtcNow;
        List<IDatabaseOperation> ops =
        [
            new CheckRecoverableIncomingMessagesOperation(_database, _runtime.Endpoints, _settings, _logger),
            new CheckRecoverableOutgoingMessagesOperation(_database, _runtime, _logger),
            // Expired, handled inbox envelopes are cleaned up on a separate, slower timer
            // (see _handledCleanupTimer / DeleteExpiredHandledEnvelopesCommand) so a large
            // cleanup delete can't block recovery work in this shared transaction (issue #3116).
            new MoveReplayableErrorMessagesToIncomingOperation(_database)
        ];

        if (_settings.Mode != DurabilityMode.Solo)
        {
            if (_database.Settings.Role == MessageStoreRole.Main)
            {
                ops.Add(new ReleaseOrphanedMessagesOperation(_database));
            }
            else if (activeNodeNumbers is { Count: > 0 })
            {
                ops.Add(new ReleaseOrphanedMessagesForAncillaryOperation(_database, activeNodeNumbers));
            }
        }

        // GH-3701: node record pruning used to live here, on the five-second recovery cadence. It now runs
        // on its own timer -- see PruneNodeRecords.

        if (_runtime.Options.Durability.OutboxStaleTime.HasValue)
        {
            ops.Add(new BumpStaleOutgoingEnvelopesOperation(new DbObjectName(_database.SchemaName, DatabaseConstants.OutgoingTable), _runtime.Options.Durability, now));
        }

        if (_runtime.Options.Durability.InboxStaleTime.HasValue)
        {
            ops.Add(new BumpStaleIncomingEnvelopesOperation(incomingTable, _runtime.Options.Durability, now));
        }

        return ops.ToArray();
    }

    public void StartScheduledJobPolling()
    {
        _scheduledJobTimer =
            new Timer(
                _ => { _runningBlock.Post(new RunScheduledMessagesOperation(_database, _settings)); },
                _settings, _settings.ScheduledJobFirstExecution, _settings.ScheduledJobPollingTime);
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        _lastHealthCheck = DateTime.UtcNow;

        // Skip the count fetch when the agent isn't running - the status check below will
        // short-circuit anyway, and a stopped agent shouldn't spin up a fresh DB query just
        // to be told the same thing.
        PersistedCounts? counts = null;
        if (Status == AgentStatus.Running)
        {
            try
            {
                counts = await _database.FetchCountsAsync();
            }
            catch (Exception e)
            {
                _health.RecordPollFailure(e);
            }
        }

        return _health.Evaluate(Status, Uri, counts, DateTimeOffset.UtcNow);
    }
}