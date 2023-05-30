using Wolverine.RDBMS.Durability;
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

    public DatabaseAgent(string databaseName, IWolverineRuntime runtime, IMessageDatabase database, DatabaseSettings databaseSettings)
    {
        _database = database;
        _databaseSettings = databaseSettings;
        _localQueue = (ILocalQueue)runtime.Endpoints.AgentForLocalQueue(TransportConstants.Scheduled);
        _settings = runtime.DurabilitySettings;

        Uri = new Uri($"{AgentScheme}://{databaseName}");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _scheduledJobTimer = new Timer(_ =>
        {
            _database.EnqueueAsync(new RunScheduledMessagesOperation(_database, _settings, _localQueue)).GetAwaiter().GetResult();
        }, _settings, _settings.ScheduledJobFirstExecution, _settings.ScheduledJobPollingTime);
        
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_scheduledJobTimer != null)
        {
            await _scheduledJobTimer.DisposeAsync();
        }
    }

    public Uri Uri { get; } 
}