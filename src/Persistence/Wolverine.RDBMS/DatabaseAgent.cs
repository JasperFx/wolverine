using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;
using Wolverine.RDBMS.Durability;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;

namespace Wolverine.RDBMS;

internal class DatabaseAgent : IAgent
{
    internal const string AgentScheme = "wolverinedb";
    
    private readonly IMessageDatabase _database;
    private readonly DatabaseSettings _databaseSettings;
    private readonly ILocalQueue _localQueue;
    private readonly DurabilitySettings _settings;
    private Timer? _scheduledJobTimer;
    private readonly ActionBlock<DatabaseOperationBatch> _runningBlock;

    public DatabaseAgent(string databaseName, IWolverineRuntime runtime, IMessageDatabase database, DatabaseSettings databaseSettings)
    {
        _database = database;
        _databaseSettings = databaseSettings;
        _localQueue = (ILocalQueue)runtime.Endpoints.AgentForLocalQueue(TransportConstants.Scheduled);
        _settings = runtime.DurabilitySettings;

        Uri = new Uri($"{AgentScheme}://{databaseName}");

        var invoker = runtime.FindInvoker(typeof(DatabaseOperationBatch));

        var logger = runtime.LoggerFactory.CreateLogger<DatabaseAgent>();

        _runningBlock = new ActionBlock<DatabaseOperationBatch>(async batch =>
        {
            if (runtime.Cancellation.IsCancellationRequested) return;
            
            try
            {
                await invoker.InvokeAsync(batch, new MessageBus(runtime));
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error trying to run durability agent commands");
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
                new RunScheduledMessagesOperation(_database, _settings, _localQueue)
            };
            
            var batch = new DatabaseOperationBatch(_database, operations);
            _runningBlock.Post(batch);

        }, _settings, _settings.ScheduledJobFirstExecution, _settings.ScheduledJobPollingTime);
        
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _runningBlock.Complete();
        
        if (_scheduledJobTimer != null)
        {
            await _scheduledJobTimer.DisposeAsync();
        }
    }

    public Uri Uri { get; } 
}