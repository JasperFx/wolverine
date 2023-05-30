using System.Threading.Tasks.Dataflow;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;

namespace Wolverine.RDBMS.Durability;

[Obsolete]
internal class DurabilityAgent : IDurabilityAgent
{
    public IMessageDatabase WolverineDatabase { get; }
    private readonly DeleteExpiredHandledEnvelopes _deleteExpired;
    private readonly bool _disabled;
    private readonly IDurabilityAction _incomingMessages;
    private readonly ILocalQueue _locals;
    private readonly ILogger _logger;
    private readonly IDurabilityAction _nodeReassignment;
    private readonly IDurabilityAction _outgoingMessages;
    private readonly MoveReplayableErrorMessagesToIncoming _moveReplayable;
    private readonly DurabilitySettings _settings;

    private readonly IMessageDatabase _database;
    private readonly ILogger<DurabilityAgent> _trace;

    private readonly ActionBlock<IDurabilityAction> _worker;

    private Timer? _nodeReassignmentTimer;
    private Timer? _scheduledJobTimer;

#pragma warning disable CS8618
    internal DurabilityAgent(IWolverineRuntime runtime, ILogger logger,
        ILogger<DurabilityAgent> trace,
        ILocalQueue locals,
        IMessageDatabase database,
        DurabilitySettings settings,
        IMessageDatabase wolverineDatabase)
#pragma warning restore CS8618)
    {
        WolverineDatabase = wolverineDatabase;


        _logger = logger;
        _trace = trace;
        _locals = locals;
        _database = database;
        _settings = settings;

        _worker = new ActionBlock<IDurabilityAction>(processActionAsync, new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = 1,
            CancellationToken = _settings.Cancellation
        });

        _incomingMessages = new RecoverIncomingMessages(logger, runtime.Endpoints);
        _outgoingMessages = new RecoverOutgoingMessages(runtime, logger);
        _nodeReassignment = new NodeReassignment(logger);
        _deleteExpired = new DeleteExpiredHandledEnvelopes();
        _moveReplayable = new MoveReplayableErrorMessagesToIncoming();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disabled)
        {
            return;
        }

        if (_database.Session.IsConnected())
        {
            await _database.Session.ReleaseNodeLockAsync(_settings.NodeLockId);
        }
        
        await _database.DisposeAsync();

        if (_scheduledJobTimer != null)
        {
            await _scheduledJobTimer.DisposeAsync();
        }

        if (_nodeReassignmentTimer != null)
        {
            await _nodeReassignmentTimer.DisposeAsync();
        }
    }

    public void EnqueueLocally(Envelope envelope)
    {
        _locals.Enqueue(envelope);
    }

    public void RescheduleIncomingRecovery()
    {
        _worker.Post(_incomingMessages);
    }

    public void RescheduleOutgoingRecovery()
    {
        _worker.Post(_outgoingMessages);
    }

    public async Task StartAsync(CancellationToken stoppingToken)
    {
        if (_disabled)
        {
            return;
        }

        await tryRestartConnectionAsync();

        _scheduledJobTimer = new Timer(_ =>
        {
            // Already obsolete
            //_worker.Post(_scheduledJobs);
            _worker.Post(_incomingMessages);
            _worker.Post(_outgoingMessages);
            _worker.Post(_deleteExpired);
            _worker.Post(_moveReplayable);
        }, _settings, _settings.ScheduledJobFirstExecution, _settings.ScheduledJobPollingTime);

        _nodeReassignmentTimer = new Timer(_ => { _worker.Post(_nodeReassignment); }, _settings,
            _settings.FirstNodeReassignmentExecution, _settings.NodeReassignmentPollingTime);
    }


    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_disabled)
        {
            return;
        }

        if (_nodeReassignmentTimer != null)
        {
            await _nodeReassignmentTimer.DisposeAsync();
        }

        if (_scheduledJobTimer != null)
        {
            await _scheduledJobTimer.DisposeAsync();
        }

        _worker.Complete();

        try
        {
            await _worker.Completion;

            await _database.Session.ReleaseNodeLockAsync(_settings.NodeLockId);

            // Release all envelopes tagged to this node in message persistence to any node
            await NodeReassignment.ReassignDormantNodeToAnyNodeAsync(_database.Session, _settings.NodeLockId,
                WolverineDatabase);

            await _database.Inbox.ReleaseIncomingAsync(_settings.NodeLockId);
        }
        catch (Exception e)
        {
            try
            {
                _logger.LogError(e, "Error while trying to stop DurabilityAgent");
            }
            catch (Exception)
            {
                Console.WriteLine(e);
            }
        }
    }

    private async Task processActionAsync(IDurabilityAction action)
    {
        if (_settings.Cancellation.IsCancellationRequested)
        {
            return;
        }

        await tryRestartConnectionAsync();

        // Something is wrong, but we'll keep trying in a bit
        if (!_database.Session.IsConnected())
        {
            return;
        }

        try
        {
            try
            {
                if (_settings.VerboseDurabilityAgentLogging)
                {
                    _trace.LogDebug("Running action {Action}", action.Description);
                }

                await action.ExecuteAsync(_database, this, _database.Session);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while running {Action}", action.Description);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to run {Action}", action);
            await _database.Session.ReleaseNodeLockAsync(_settings.NodeLockId);
        }

        await _database.Session.GetNodeLockAsync(_settings.NodeLockId);
    }

    private async Task tryRestartConnectionAsync()
    {
        if (_database.Session.IsConnected())
        {
            return;
        }

        try
        {
            await _database.Session.ConnectAndLockCurrentNodeAsync(_logger, _settings.NodeLockId);
        }
        catch (Exception? e)
        {
            _logger.LogError(e, "Failure trying to restart the connection in DurabilityAgent");
        }
    }
}