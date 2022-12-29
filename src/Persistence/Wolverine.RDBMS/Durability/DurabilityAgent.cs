using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;

namespace Wolverine.RDBMS.Durability;

internal class DurabilityAgent : IDurabilityAgent
{
    public DatabaseSettings DatabaseSettings { get; }
    private readonly DeleteExpiredHandledEnvelopes _deleteExpired;
    private readonly bool _disabled;
    private readonly IDurabilityAction _incomingMessages;
    private readonly ILocalQueue _locals;
    private readonly ILogger _logger;
    private readonly MetricsCalculator _metrics;
    private readonly IDurabilityAction _nodeReassignment;
    private readonly IDurabilityAction _outgoingMessages;
    private readonly IDurabilityAction _scheduledJobs;
    private readonly NodeSettings _settings;

    private readonly IMessageStore _storage;
    private readonly ILogger<DurabilityAgent> _trace;

    private readonly ActionBlock<IDurabilityAction> _worker;

    private Timer? _nodeReassignmentTimer;
    private Timer? _scheduledJobTimer;

#pragma warning disable CS8618
    internal DurabilityAgent(IWolverineRuntime runtime, ILogger logger,
        ILogger<DurabilityAgent> trace,
        ILocalQueue locals,
        IMessageStore storage,
        NodeSettings settings, 
        DatabaseSettings databaseSettings)
#pragma warning restore CS8618)
    {
        DatabaseSettings = databaseSettings;
        if (storage is NullMessageStore)
        {
            _disabled = true;
            return;
        }

        _logger = logger;
        _trace = trace;
        _locals = locals;
        _storage = storage;
        _settings = settings;

        _metrics = new MetricsCalculator(runtime.Meter);

        _worker = new ActionBlock<IDurabilityAction>(processActionAsync, new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = 1,
            CancellationToken = _settings.Cancellation
        });

        _incomingMessages = new RecoverIncomingMessages(logger, runtime.Endpoints);
        _outgoingMessages = new RecoverOutgoingMessages(runtime, logger);
        _nodeReassignment = new NodeReassignment();
        _deleteExpired = new DeleteExpiredHandledEnvelopes();
        _scheduledJobs = new RunScheduledJobs(settings, logger);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disabled)
        {
            return;
        }

        if (_storage.Session.IsConnected())
        {
            await _storage.Session.ReleaseNodeLockAsync(_settings.UniqueNodeId);
            _storage.SafeDispose();
        }

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
            _worker.Post(_scheduledJobs);
            _worker.Post(_incomingMessages);
            _worker.Post(_outgoingMessages);
            _worker.Post(_metrics);
            _worker.Post(_deleteExpired);
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

            await _storage.Session.ReleaseNodeLockAsync(_settings.UniqueNodeId);

            // Release all envelopes tagged to this node in message persistence to any node
            await NodeReassignment.ReassignDormantNodeToAnyNodeAsync(_storage.Session, _settings.UniqueNodeId, DatabaseSettings);
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
        if (!_storage.Session.IsConnected())
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

                await action.ExecuteAsync(_storage, this, _settings, DatabaseSettings);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while running {Action}", action.Description);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to run {Action}", action);
            await _storage.Session.ReleaseNodeLockAsync(_settings.UniqueNodeId);
        }

        await _storage.Session.GetNodeLockAsync(_settings.UniqueNodeId);
    }

    private async Task tryRestartConnectionAsync()
    {
        if (_storage.Session.IsConnected())
        {
            return;
        }

        try
        {
            await _storage.Session.ConnectAndLockCurrentNodeAsync(_logger, _settings.UniqueNodeId);
        }
        catch (Exception? e)
        {
            _logger.LogError(e, "Failure trying to restart the connection in DurabilityAgent");
        }
    }
}