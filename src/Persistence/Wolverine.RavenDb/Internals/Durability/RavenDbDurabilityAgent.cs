using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;

namespace Wolverine.RavenDb.Internals.Durability;

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

    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationTokenSource _combined;
    private PersistenceMetrics _metrics;
    
    public RavenDbDurabilityAgent(IDocumentStore store, IWolverineRuntime runtime, RavenDbMessageStore parent)
    {
        _store = store;
        _runtime = runtime;
        _parent = parent;
        _localQueue = (ILocalQueue)runtime.Endpoints.AgentForLocalQueue(TransportConstants.Scheduled);
        _settings = runtime.DurabilitySettings;

        Uri = new Uri("ravendb://durability");

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
        _metrics = new PersistenceMetrics(_runtime.Meter, _settings, null);
        
        if (_settings.DurabilityMetricsEnabled)
        {
            _metrics.StartPolling(_runtime.LoggerFactory.CreateLogger<PersistenceMetrics>(), _parent);
        }
        
        var recoveryStart = _settings.ScheduledJobFirstExecution.Add(new Random().Next(0, 1000).Milliseconds());

        _recoveryTask = Task.Run(async () =>
        {
            await Task.Delay(recoveryStart, _combined.Token);
            using var timer = new PeriodicTimer(_settings.ScheduledJobPollingTime);

            while (!_combined.IsCancellationRequested)
            {
                var lastExpiredTime = DateTimeOffset.UtcNow;
                
                await tryRecoverIncomingMessages();
                await tryRecoverOutgoingMessagesAsync();

                if (_settings.DeadLetterQueueExpirationEnabled)
                {
                    // Crudely just doing this every hour
                    var now = DateTimeOffset.UtcNow;
                    if (now > lastExpiredTime.AddHours(1))
                    {
                        await tryDeleteExpiredDeadLetters();
                    }
                }

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

    private async Task tryDeleteExpiredDeadLetters()
    {
        var now = DateTimeOffset.UtcNow;
        using var session = _store.OpenAsyncSession();
        var op =
            await _store.Operations.SendAsync(
                new DeleteByQueryOperation<DeadLetterMessage>("DeadLetterMessages", x => x.ExpirationTime < now));
 
        await op.WaitForCompletionAsync();
    }


    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cancellation.Cancel();

        if (_metrics != null)
        {
            _metrics.SafeDispose();
        }
        
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