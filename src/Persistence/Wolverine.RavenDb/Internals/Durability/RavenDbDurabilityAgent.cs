using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;

namespace Wolverine.RavenDb.Internals.Durability;


// TODO -- use a subscription for replayable dlq messages
// TODO -- try to use RavenDb's internal document expiry for expired envelopes
// TODO -- use a subscription on the leader for outgoing messages marked as any node?

/*
            var operations = new IDatabaseOperation[]
   {
       new DeleteExpiredEnvelopesOperation(
           new DbObjectName(_database.SchemaName, DatabaseConstants.IncomingTable), DateTimeOffset.UtcNow),
       new MoveReplayableErrorMessagesToIncomingOperation(_database)
   };

 */

public partial class RavenDbDurabilityAgent : IAgent
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
                await tryRecoverIncomingMessages();
                await tryRecoverOutgoingMessagesAsync();
                
                await timer.WaitForNextTickAsync(_combined.Token);
            }
        }, _combined.Token);

        _scheduledJob = Task.Run(async () =>
        {
            await Task.Delay(recoveryStart, _combined.Token);
            using var timer = new PeriodicTimer(_settings.ScheduledJobPollingTime);
            
            while (!_combined.IsCancellationRequested)
            {
                await runScheduledJobs();
                await timer.WaitForNextTickAsync(_combined.Token);
            }
        }, _combined.Token);
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