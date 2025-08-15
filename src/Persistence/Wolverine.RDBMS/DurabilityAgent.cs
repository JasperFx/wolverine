using JasperFx.Blocks;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.Persistence;
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
    private readonly ILocalQueue _localQueue;
    private readonly ILogger<DurabilityAgent> _logger;
    private readonly Block<IAgentCommand> _runningBlock;

    private readonly IWolverineRuntime _runtime;
    private readonly DurabilitySettings _settings;
    private Timer? _expirationTimer;
    private DateTimeOffset? _lastDeadLetterQueueCheck;

    private DateTimeOffset? _lastNodeRecordPruneTime;
    private PersistenceMetrics _metrics;
    private Timer? _recoveryTimer;
    private Timer? _scheduledJobTimer;

    public DurabilityAgent(string databaseName, IWolverineRuntime runtime, IMessageDatabase database)
    {
        _runtime = runtime;
        _database = database;
        _localQueue = (ILocalQueue)runtime.Endpoints.AgentForLocalQueue(TransportConstants.Scheduled);
        _settings = runtime.DurabilitySettings;

        Uri = database.Uri;

        var executor = runtime.As<IExecutorFactory>().BuildFor(typeof(IAgentCommand));

        _logger = runtime.LoggerFactory.CreateLogger<DurabilityAgent>();

        _runningBlock = new Block<IAgentCommand>(async batch =>
        {
            if (runtime.Cancellation.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await executor.InvokeAsync(batch, new MessageBus(runtime));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to run durability agent commands");
            }
        });
    }

    public bool AutoStartScheduledJobPolling { get; set; } = false;

    public AgentStatus Status { get; set; } = AgentStatus.Started;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _metrics = new PersistenceMetrics(_runtime.Meter, _settings, _database.Name);

        if (_settings.DurabilityMetricsEnabled)
        {
            _metrics.StartPolling(_runtime.LoggerFactory.CreateLogger<PersistenceMetrics>(), _database);
        }

        var recoveryStart = _settings.ScheduledJobFirstExecution.Add(new Random().Next(0, 1000).Milliseconds());

        _recoveryTimer = new Timer(_ =>
        {
            var operations = buildOperationBatch();

            var batch = new DatabaseOperationBatch(_database, operations);
            _runningBlock.Post(batch);
        }, _settings, recoveryStart, _settings.ScheduledJobPollingTime);

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

        if (AutoStartScheduledJobPolling)
        {
            StartScheduledJobPolling();
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _runningBlock.Complete();
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

        Status = AgentStatus.Stopped;
    }

    public Uri Uri { get; internal set; }

    public static Uri SimplifyUri(Uri uri)
    {
        return new Uri($"{PersistenceConstants.AgentScheme}://{uri.Host}");
    }

    public static Uri AddMarkerType(Uri uri, Type markerType)
    {
        return new Uri($"{uri}{markerType.Name}");
    }

    private bool isTimeToPruneNodeEventRecords()
    {
        if (_lastNodeRecordPruneTime == null)
        {
            return true;
        }

        if (DateTimeOffset.UtcNow.Subtract(_lastNodeRecordPruneTime.Value) > 1.Hours())
        {
            return true;
        }

        return false;
    }

    private IDatabaseOperation[] buildOperationBatch()
    {
        if (_database.Settings.IsMain && isTimeToPruneNodeEventRecords())
        {
            return
            [
                new CheckRecoverableIncomingMessagesOperation(_database, _runtime.Endpoints, _settings, _logger),
                new CheckRecoverableOutgoingMessagesOperation(_database, _runtime, _logger),
                new DeleteExpiredEnvelopesOperation(
                    new DbObjectName(_database.SchemaName, DatabaseConstants.IncomingTable), DateTimeOffset.UtcNow),
                new MoveReplayableErrorMessagesToIncomingOperation(_database),
                new DeleteOldNodeEventRecords(_database, _settings)
            ];
        }

        return
        [
            new CheckRecoverableIncomingMessagesOperation(_database, _runtime.Endpoints, _settings, _logger),
            new CheckRecoverableOutgoingMessagesOperation(_database, _runtime, _logger),
            new DeleteExpiredEnvelopesOperation(
                new DbObjectName(_database.SchemaName, DatabaseConstants.IncomingTable), DateTimeOffset.UtcNow),
            new MoveReplayableErrorMessagesToIncomingOperation(_database)
        ];
    }

    public void StartScheduledJobPolling()
    {
        _scheduledJobTimer =
            new Timer(
                _ => { _runningBlock.Post(new RunScheduledMessagesOperation(_database, _settings, _localQueue)); },
                _settings, _settings.ScheduledJobFirstExecution, _settings.ScheduledJobPollingTime);
    }
}