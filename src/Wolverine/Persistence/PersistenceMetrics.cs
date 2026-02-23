using System.Diagnostics.Metrics;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.Persistence;

public class PersistenceMetrics : IDisposable
{
    private readonly DurabilitySettings _settings;
    private readonly ObservableGauge<int> _incoming;
    private readonly ObservableGauge<int> _outgoing;
    private readonly ObservableGauge<int> _scheduled;
    private CancellationTokenSource _cancellation;
    private Task _task;
    private readonly IWolverineObserver _observer;

    public PersistenceMetrics(IWolverineRuntime runtime, DurabilitySettings settings, string? databaseName)
    {
        _settings = settings;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(settings.Cancellation);
        _observer = runtime.Observer;
        var meter = runtime.Meter;

        if (databaseName.IsEmpty())
        {
            _incoming = meter.CreateObservableGauge(MetricsConstants.InboxCount, () => Counts.Incoming,
                MetricsConstants.Messages, "Inbox messages");
            _outgoing = meter.CreateObservableGauge(MetricsConstants.OutboxCount, () => Counts.Outgoing,
                MetricsConstants.Messages, "Outbox messages");
            _scheduled = meter.CreateObservableGauge(MetricsConstants.ScheduledCount, () => Counts.Scheduled,
                MetricsConstants.Messages, "Scheduled messages");
        }
        else
        {
            _incoming = meter.CreateObservableGauge( MetricsConstants.InboxCount + "." + databaseName, () => Counts.Incoming,
                MetricsConstants.Messages, "Inbox messages for database " + databaseName);
            _outgoing = meter.CreateObservableGauge(MetricsConstants.OutboxCount+ "." + databaseName, () => Counts.Outgoing,
                MetricsConstants.Messages, "Outbox messages for database " + databaseName);
            _scheduled = meter.CreateObservableGauge(MetricsConstants.ScheduledCount+ "." + databaseName, () => Counts.Scheduled,
                MetricsConstants.Messages, "Scheduled messages for database " + databaseName);
        }
    }
    
    public PersistedCounts Counts { get; set; } = new();

    public void StartPolling(ILogger logger, IMessageStore store)
    {
        _task = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(_settings.UpdateMetricsPeriod);
            
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    Counts = await store.Admin.FetchCountsAsync();
                    _observer.PersistedCounts(store.Uri, Counts);
                }
                catch (TaskCanceledException)
                {
                    continue;
                }
                catch (OperationCanceledException)
                {
                    continue;
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error trying to update the metrics on envelope storage for {Store}", store);
                }
                
                await timer.WaitForNextTickAsync(_cancellation.Token);
            }
        });
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _task?.SafeDispose();
    }
}