using System.Threading.Tasks.Dataflow;
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

    private readonly IWolverineRuntime _runtime;
    private readonly IMessageDatabase _database;
    private readonly DatabaseSettings _databaseSettings;
    private readonly ILocalQueue _localQueue;
    private readonly DurabilitySettings _settings;
    private Timer? _scheduledJobTimer;
    private readonly ActionBlock<IAgentCommand> _runningBlock;
    private ILogger<DurabilityAgent> _logger;
    private Timer? _reassignmentTimer;

    public DurabilityAgent(string databaseName, IWolverineRuntime runtime, IMessageDatabase database)
    {
        _runtime = runtime;
        _database = database;
        _databaseSettings = database.Settings;
        _localQueue = (ILocalQueue)runtime.Endpoints.AgentForLocalQueue(TransportConstants.Scheduled);
        _settings = runtime.DurabilitySettings;

        Uri = new Uri($"{AgentScheme}://{databaseName}");

        var executor = runtime.As<IExecutorFactory>().BuildFor(typeof(IAgentCommand));

        _logger = runtime.LoggerFactory.CreateLogger<DurabilityAgent>();

        _runningBlock = new ActionBlock<IAgentCommand>(async batch =>
        {
            if (runtime.Cancellation.IsCancellationRequested) return;
            
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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _scheduledJobTimer = new Timer(_ =>
        {
            var operations = new IDatabaseOperation[]
            {
                new RunScheduledMessagesOperation(_database, _settings, _localQueue),
                new CheckRecoverableIncomingMessagesOperation(_database, _runtime.Endpoints, _settings, _logger),
                new CheckRecoverableOutgoingMessagesOperation(_database, _runtime, _logger),
                new DeleteExpiredEnvelopesOperation(new DbObjectName(_database.SchemaName, DatabaseConstants.IncomingTable), DateTimeOffset.UtcNow),
                new MoveReplayableErrorMessagesToIncomingOperation(_database)
            };
            
            var batch = new DatabaseOperationBatch(_database, operations);
            _runningBlock.Post(batch);
        }, _settings, _settings.ScheduledJobFirstExecution, _settings.ScheduledJobPollingTime);

        _reassignmentTimer = new Timer(_ =>
        {
            _runningBlock.Post(new ReassignDormantNodes(_runtime.Storage.Nodes, _database, _logger));
        }, _settings, _settings.FirstNodeReassignmentExecution, _settings.NodeReassignmentPollingTime);

        return Task.CompletedTask;
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
    }

    public Uri Uri { get; } 
}