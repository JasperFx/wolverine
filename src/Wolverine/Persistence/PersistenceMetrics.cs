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
    private Task _task = null!;
    private readonly IWolverineObserver _observer;

    public PersistenceMetrics(IWolverineRuntime runtime, DurabilitySettings settings, string? databaseName)
    {
        _settings = settings;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(settings.Cancellation);
        _observer = runtime.Observer;
        var meter = runtime.Meter;

        // GH-3225: tag the queue-depth gauges dimensionally so an external TSDB can slice inbox/outbox/scheduled
        // depth per service ("source") and per database. The database is a dimensional tag rather than a per-database
        // instrument *name* suffix (the old behavior) so a single series can be grouped/filtered by database.
        var tags = new List<KeyValuePair<string, object?>>
        {
            new(MetricsConstants.SourceKey, runtime.Options.ServiceName)
        };

        if (databaseName.IsNotEmpty())
        {
            tags.Add(new KeyValuePair<string, object?>(MetricsConstants.DatabaseKey, databaseName));
        }

        _incoming = meter.CreateObservableGauge(MetricsConstants.InboxCount, () => Counts.Incoming,
            MetricsConstants.Messages, "Inbox messages", tags);
        _outgoing = meter.CreateObservableGauge(MetricsConstants.OutboxCount, () => Counts.Outgoing,
            MetricsConstants.Messages, "Outbox messages", tags);
        _scheduled = meter.CreateObservableGauge(MetricsConstants.ScheduledCount, () => Counts.Scheduled,
            MetricsConstants.Messages, "Scheduled messages", tags);
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
        _cancellation.Dispose();
        _task?.SafeDispose();
    }
}