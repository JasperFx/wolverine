using System.Threading.Tasks.Dataflow;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.RDBMS.Durability;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;

namespace Wolverine.RDBMS;

internal class DurabilityAgent : IAgent
{
    internal const string AgentScheme = "wolverinedb";
    private readonly IMessageDatabase _database;
    private readonly ILocalQueue _localQueue;
    private readonly ActionBlock<IAgentCommand> _runningBlock;

    private readonly IWolverineRuntime _runtime;
    private readonly DurabilitySettings _settings;
    private readonly ILogger<DurabilityAgent> _logger;
    private Timer? _reassignmentTimer;
    private Timer? _scheduledJobTimer;
    private Timer? _recoveryTimer;

    public DurabilityAgent(string databaseName, IWolverineRuntime runtime, IMessageDatabase database)
    {
        _runtime = runtime;
        _database = database;
        _localQueue = (ILocalQueue)runtime.Endpoints.AgentForLocalQueue(TransportConstants.Scheduled);
        _settings = runtime.DurabilitySettings;

        Uri = new Uri($"{AgentScheme}://{databaseName}");

        var executor = runtime.As<IExecutorFactory>().BuildFor(typeof(IAgentCommand));

        _logger = runtime.LoggerFactory.CreateLogger<DurabilityAgent>();

        _runningBlock = new ActionBlock<IAgentCommand>(async batch =>
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
        }, new ExecutionDataflowBlockOptions
        {
            EnsureOrdered = true,
            MaxDegreeOfParallelism = 1,
            CancellationToken = runtime.Cancellation
        });
    }

    public AgentStatus Status { get; set; } = AgentStatus.Started;

    public static Uri SimplifyUri(Uri uri)
    {
        return new Uri($"{DurabilityAgent.AgentScheme}://{uri.Host}");
    }

    public static Uri AddMarkerType(Uri uri, Type markerType)
    {
        return new Uri($"{uri}{markerType.Name}");
    }

    public bool AutoStartScheduledJobPolling { get; set; } = false;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var recoveryStart = _settings.ScheduledJobFirstExecution.Add(new Random().Next(0, 1000).Milliseconds());

        _recoveryTimer = new Timer(_ =>
        {
            var operations = new IDatabaseOperation[]
            {
                new CheckRecoverableIncomingMessagesOperation(_database, _runtime.Endpoints, _settings, _logger),
                new CheckRecoverableOutgoingMessagesOperation(_database, _runtime, _logger),
                new DeleteExpiredEnvelopesOperation(
                    new DbObjectName(_database.SchemaName, DatabaseConstants.IncomingTable), DateTimeOffset.UtcNow),
                new MoveReplayableErrorMessagesToIncomingOperation(_database)
            };

            var batch = new DatabaseOperationBatch(_database, operations);
            _runningBlock.Post(batch);
        }, _settings, recoveryStart, _settings.ScheduledJobPollingTime);

        if (_settings.DurabilityAgentEnabled && _settings.Mode == DurabilityMode.Balanced)
        {
            _reassignmentTimer =
                new Timer(
                    _ => { _runningBlock.Post(new ReassignDormantNodes(_runtime.Storage.Nodes, _database)); },
                    _settings, _settings.FirstNodeReassignmentExecution, _settings.NodeReassignmentPollingTime);
        }

        if (AutoStartScheduledJobPolling)
        {
            StartScheduledJobPolling();
        }

        return Task.CompletedTask;
    }

    public void StartScheduledJobPolling()
    {
        _scheduledJobTimer =
            new Timer(_ => { _runningBlock.Post(new RunScheduledMessagesOperation(_database, _settings, _localQueue)); },
                _settings, _settings.ScheduledJobFirstExecution, _settings.ScheduledJobPollingTime);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _runningBlock.Complete();

        if (_scheduledJobTimer != null)
        {
            await _scheduledJobTimer.DisposeAsync();
        }

        if (_reassignmentTimer != null)
        {
            await _reassignmentTimer.DisposeAsync();
        }

        if (_recoveryTimer != null)
        {
            await _recoveryTimer.DisposeAsync();
        }

        Status = AgentStatus.Stopped;
    }

    public Uri Uri { get; internal set; }
}