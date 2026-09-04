using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Wolverine.Logging;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
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
    private PersistenceMetrics _metrics = null!;
    private readonly DurabilityHealthSignals _health;

    public RavenDbDurabilityAgent(IDocumentStore store, IWolverineRuntime runtime, RavenDbMessageStore parent)
    {
        _store = store;
        _runtime = runtime;
        _parent = parent;
        _localQueue = (ILocalQueue)runtime.Endpoints.AgentForLocalQueue(TransportConstants.Scheduled);
        _settings = runtime.DurabilitySettings;

        Uri = new Uri($"{PersistenceConstants.AgentScheme}://ravendb/durability");

        _logger = runtime.LoggerFactory.CreateLogger<RavenDbDurabilityAgent>();

        _combined = CancellationTokenSource.CreateLinkedTokenSource(runtime.Cancellation, _cancellation.Token);
        _health = new DurabilityHealthSignals(_settings);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartTimers();

        return Task.CompletedTask;
    }

    internal void StartTimers()
    {
        _metrics = new PersistenceMetrics(_runtime, _settings, null);
        
        if (_settings.DurabilityMetricsEnabled)
        {
            _metrics.StartPolling(_runtime.LoggerFactory.CreateLogger<PersistenceMetrics>(), _parent);
        }
        
        var recoveryStart = _settings.ScheduledJobFirstExecution.Add(new Random().Next(0, 1000).Milliseconds());

        _recoveryTask = Task.Run(async () =>
        {
            await Task.Delay(recoveryStart, _combined.Token);
            using var timer = new PeriodicTimer(_settings.ScheduledJobPollingTime);

            // GH-4286: this throttle lives OUTSIDE the loop — reset per iteration, the hourly guard
            // below could only fire if a single recovery tick took more than an hour, so expired dead
            // letters were never deleted. MinValue makes the first tick sweep immediately, matching the
            // RDBMS providers' expiration timer that first fires a minute after startup.
            var lastExpiredTime = DateTimeOffset.MinValue;

            while (!_combined.IsCancellationRequested)
            {
                try
                {
                    await tryRecoverIncomingMessages();
                    await tryRecoverOutgoingMessagesAsync();

                    if (_settings.DeadLetterQueueExpirationEnabled)
                    {
                        // Crudely just doing this every hour
                        var now = DateTimeOffset.UtcNow;
                        if (now > lastExpiredTime.AddHours(1))
                        {
                            await tryDeleteExpiredDeadLetters();
                            lastExpiredTime = now;
                        }
                    }

                    _health.RecordPollSuccess();
                }
                catch (Exception e) when (!_combined.IsCancellationRequested)
                {
                    _health.RecordPollFailure(e);
                    _logger.LogError(e, "Recovery loop tick failed");
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
                try
                {
                    await runScheduledJobs();
                    _health.RecordPollSuccess();
                }
                catch (Exception e) when (!_combined.IsCancellationRequested)
                {
                    _health.RecordPollFailure(e);
                    _logger.LogError(e, "Scheduled-job loop tick failed");
                }

                await timer.WaitForNextTickAsync(_combined.Token);
            }
        }, _combined.Token);

    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        PersistedCounts? counts = null;
        if (Status == AgentStatus.Running)
        {
            try
            {
                counts = await _parent.Admin.FetchCountsAsync();
            }
            catch (Exception e)
            {
                _health.RecordPollFailure(e);
            }
        }

        return _health.Evaluate(Status, Uri, counts, DateTimeOffset.UtcNow);
    }

    private async Task tryDeleteExpiredDeadLetters()
    {
        // GH-4286: this used to be a DeleteByQueryOperation against an index named "DeadLetterMessages"
        // that Wolverine never creates, so once the throttle defect above was fixed and the sweep could
        // actually fire, it threw IndexDoesNotExistException instead of deleting. A dynamic query builds
        // its own auto index, the same way runScheduledJobs queries IncomingMessage.
        var now = DateTimeOffset.UtcNow;

        while (!_combined.IsCancellationRequested)
        {
            using var session = _store.OpenAsyncSession();
            var expired = await session.Query<DeadLetterMessage>()
                .Where(x => x.ExpirationTime < now)
                .Take(_settings.RecoveryBatchSize)
                .ToListAsync(_combined.Token);

            if (!expired.Any())
            {
                return;
            }

            foreach (var message in expired)
            {
                session.Delete(message);
            }

            await session.SaveChangesAsync(_combined.Token);

            if (expired.Count < _settings.RecoveryBatchSize)
            {
                return;
            }
        }
    }


    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cancellation.CancelAsync();

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
    }

    public Uri Uri { get; set; }
    public AgentStatus Status { get; set; }

    /// <summary>
    /// Human-readable description for monitoring tools — see
    /// <see cref="IAgent.Description"/>.
    /// </summary>
    public string Description => $"Wolverine RavenDB durability agent for {Uri} — recovers persisted inbox/outbox messages and runs scheduled jobs against the RavenDB message store.";

    /// <summary>
    /// True once <see cref="StartTimers"/> has wired up the recovery and scheduled-job
    /// background loops. Exposed for diagnostic and test inspection so callers can detect
    /// the multi-instance "two pollers" condition without reflection. See #2623.
    /// </summary>
    public bool IsPolling => _recoveryTask is not null || _scheduledJob is not null;
}