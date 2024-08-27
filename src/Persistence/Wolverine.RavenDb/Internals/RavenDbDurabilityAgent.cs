using System.Threading.Tasks.Dataflow;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;

namespace Wolverine.RavenDb.Internals;

public class RavenDbDurabilityAgent : IAgent
{
    private readonly IDocumentStore _store;
    private readonly IWolverineRuntime _runtime;
    private readonly RavenDbMessageStore _parent;
    private readonly ILocalQueue _localQueue;
    private readonly DurabilitySettings _settings;
    private readonly ILogger<RavenDbDurabilityAgent> _logger;

    private Task? _recoveryTask;
    private Task? _scheduledJob;
    private readonly IExecutor _executor;

    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationTokenSource _combined;

    public RavenDbDurabilityAgent(IDocumentStore store, IWolverineRuntime runtime, RavenDbMessageStore parent)
    {
        _store = store;
        _runtime = runtime;
        _parent = parent;
        _localQueue = (ILocalQueue)runtime.Endpoints.AgentForLocalQueue(TransportConstants.Scheduled);
        _settings = runtime.DurabilitySettings;

        Uri = new Uri($"ravendb://durability");

        _executor = runtime.As<IExecutorFactory>().BuildFor(typeof(IAgentCommand));

        _logger = runtime.LoggerFactory.CreateLogger<RavenDbDurabilityAgent>();
        
        _combined = CancellationTokenSource.CreateLinkedTokenSource(runtime.Cancellation, _cancellation.Token);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartTimers();

        return Task.CompletedTask;
    }

    internal void StartTimers()
    {
        var recoveryStart = _settings.ScheduledJobFirstExecution.Add(new Random().Next(0, 1000).Milliseconds());

        _recoveryTask = Task.Run(async () =>
        {
            await Task.Delay(recoveryStart, _combined.Token);
            using var timer = new PeriodicTimer(_settings.ScheduledJobPollingTime);
            
            while (!_combined.IsCancellationRequested)
            {
                await tryRecoverMessages();
                await timer.WaitForNextTickAsync(_combined.Token);
            }
        }, _combined.Token);

        _scheduledJob = Task.Run(async () =>
        {
            await Task.Delay(_settings.ScheduledJobFirstExecution, _combined.Token);
            using var timer = new PeriodicTimer(_settings.ScheduledJobPollingTime);
            
            while (!_combined.IsCancellationRequested)
            {
                await runScheduledJobs();
                await timer.WaitForNextTickAsync(_combined.Token);
            }
        }, _combined.Token);
    }

    private async Task tryRecoverMessages()
    {
        // TODO -- use a subscription for replayable dlq messages
        // TODO -- try to use RavenDb's internal document expiry for expired envelopes
        // TODO -- use a subscription on the leader for outgoing messages marked as any node?

/*
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
 */
    }

    private async Task runScheduledJobs()
    {
        if (!(await _parent.TryAttainScheduledJobLockAsync(_combined.Token)))
        {
            return;
        }
        
        try
        {
            using var session = _store.OpenAsyncSession();
            var incoming = await session.Query<IncomingMessage>()
                .Where(x => x.Status == EnvelopeStatus.Scheduled && x.ExecutionTime <= DateTimeOffset.UtcNow)
                .OrderBy(x => x.ExecutionTime)
                .Take(_settings.RecoveryBatchSize)
                .ToListAsync(_combined.Token);

            if (!incoming.Any())
            {
                return;
            }
            
            var envelopes = incoming.Select(x => x.Read()).ToList();

            foreach (var message in incoming)
            {
                message.Status = EnvelopeStatus.Incoming;
                message.OwnerId = _settings.AssignedNodeNumber;
            }
            
            await session.SaveChangesAsync();

            // This is very low risk
            foreach (var envelope in envelopes)
            {
                _logger.LogInformation("Locally enqueuing scheduled message {Id} of type {MessageType}", envelope.Id,
                    envelope.MessageType);
                _localQueue.Enqueue(envelope);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while trying to process ");
        }
        finally
        {
            await _parent.ReleaseScheduledJobLockAsync();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cancellation.Cancel();

        if (_recoveryTask != null)
        {
            _recoveryTask.SafeDispose();
        }

        if (_scheduledJob != null)
        {
            _scheduledJob.SafeDispose();
        }

        return Task.CompletedTask;
    }

    public Uri Uri { get; set; }
    public AgentStatus Status { get; set; }
}