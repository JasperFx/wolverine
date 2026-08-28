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
    private Timer? _orphanSweepTimer;
    private Timer? _deduplicationCleanupTimer;

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
        _recoveryTimer = new Timer(_ =>
        {
            var batch = new DatabaseOperationBatch(_database, buildOperationBatch());
            _runningBlock.Post(batch);
        }, _settings, recoveryStart, _settings.ScheduledJobPollingTime);

        // GH-3971: the orphaned-message sweep used to be appended to the recovery batch above, which put
        // an unbounded full-table UPDATE inside the shared recovery transaction and tied its cadence to
        // ScheduledJobPollingTime -- so slowing it down also delayed scheduled message delivery. It now
        // runs on its own timer in its own transaction, exactly as #3116 did for the expired-handled
        // cleanup. Solo mode has no peers to orphan anything, so the timer is never created.
        if (_settings.Mode != DurabilityMode.Solo)
        {
            _orphanSweepTimer = new Timer(async _ =>
            {
                try
                {
                    var sweep = await buildOrphanSweepAsync();
                    if (sweep != null) _runningBlock.Post(sweep);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error building the orphaned message sweep for database {Database}",
                        _database.Name);
                }
            }, _settings, recoveryStart, _settings.OrphanedMessageSweepPollingTime);
        }
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

        // GH-4180: the deduplication table is append-only on the write path -- one row per deduplicated
        // message -- so enabling the feature without a reaper trades duplicate work for a table that
        // grows without bound. Its own timer, its own transaction, for the same reason as the handled
        // cleanup above. Created only when the feature is on, so nothing changes for anyone else.
        if (_settings.EnableMessageDeduplication)
        {
            _deduplicationCleanupTimer = new Timer(_ =>
            {
                _runningBlock.Post(new DeleteExpiredDeduplicationClaimsCommand(_database, _logger));
            }, _settings, _settings.DeduplicationCleanupPollingTime, _settings.DeduplicationCleanupPollingTime);
        }

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

        if (_orphanSweepTimer != null)
        {
            await _orphanSweepTimer.DisposeAsync();
        }

        if (_deduplicationCleanupTimer != null)
        {
            await _deduplicationCleanupTimer.DisposeAsync();
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

    /// <summary>
    /// GH-3971: build the orphaned-message sweep for this database. Runs on its own timer in its own
    /// transaction -- see <see cref="ReleaseOrphanedMessagesCommand"/> for why it is no longer part of
    /// <see cref="buildOperationBatch"/>.
    /// </summary>
    internal async Task<IAgentCommand?> buildOrphanSweepAsync()
    {
        // A Solo node is the whole cluster: there are no peers whose departure could orphan anything, and
        // releasing on its own restart is what GH-3287 established must NOT happen. The timer is not even
        // created in Solo mode, but the rule lives here so there is exactly one place that decides it.
        if (_settings.Mode == DurabilityMode.Solo) return null;

        if (_database.Settings.Role == MessageStoreRole.Main)
        {
            // The node table is in this same database, so the command reads the live node numbers itself
            // inside the same connection as the owner scan.
            return new ReleaseOrphanedMessagesCommand(_database, _settings, _logger);
        }

        // An ancillary database has no wolverine_nodes table of its own, so the live node numbers have to
        // come from the main store.
        //
        // Node-wide, not per-database: LoadAllNodesAsync also selects the whole assignment table to
        // populate ActiveAgents, which this caller never reads, and there is one durability agent per
        // message database. See ActiveNodeNumberCache.
        var cache = ActiveNodeNumberCache.For(_runtime);
        var activeNodeNumbers = await cache.FetchAsync(_runtime.Cancellation);

        // GH-3850: the list is up to one polling interval old, so it cannot speak for a node that
        // registered after it was taken. The mark bounds who it may judge.
        return new ReleaseOrphanedMessagesCommand(_database, _settings, _logger, activeNodeNumbers,
            cache.HighWaterMark);
    }

    internal IDatabaseOperation[] buildOperationBatch()
    {
        var incomingTable = _database.DbObjectNameFor(DatabaseConstants.IncomingTable);
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

        // GH-3701: node record pruning used to live here, on the five-second recovery cadence. It now runs
        // on its own timer -- see PruneNodeRecords.
        //
        // GH-3971: so does the orphaned-message sweep, for the same reason plus two more -- it was an
        // unbounded UPDATE holding the shared recovery transaction, and its predicate could not use an
        // index. See buildOrphanSweepAsync / ReleaseOrphanedMessagesCommand.

        if (_runtime.Options.Durability.OutboxStaleTime.HasValue)
        {
            ops.Add(new BumpStaleOutgoingEnvelopesOperation(_database.DbObjectNameFor(DatabaseConstants.OutgoingTable), _runtime.Options.Durability, now));
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