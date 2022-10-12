using System.Diagnostics.Metrics;
using System.Threading.Tasks;
using Wolverine.Logging;

namespace Wolverine.Persistence.Durability;

internal class MetricsCalculator : IMessagingAction
{
    private readonly ObservableGauge<int> _incoming;
    private readonly ObservableGauge<int> _outgoing;
    private readonly ObservableGauge<int> _scheduled;

    public MetricsCalculator(Meter meter)
    {
        _incoming = meter.CreateObservableGauge<int>(MetricsConstants.InboxCount, () => Counts.Incoming, MetricsConstants.Messages, "Inbox messages");
        _outgoing = meter.CreateObservableGauge<int>(MetricsConstants.OutboxCount, () => Counts.Outgoing, MetricsConstants.Messages, "Outbox messages");
        _scheduled = meter.CreateObservableGauge<int>(MetricsConstants.ScheduledCount, () => Counts.Scheduled, MetricsConstants.Messages, "Scheduled messages");
    }
    
    public string Description { get; } = "Metrics collection of inbox and outbox";
    public async Task ExecuteAsync(IEnvelopePersistence storage, IDurabilityAgent agent)
    {
        var counts = await storage.Admin.FetchCountsAsync();
        Counts = counts;
    }

    public PersistedCounts Counts { get; private set; } = new PersistedCounts();
}