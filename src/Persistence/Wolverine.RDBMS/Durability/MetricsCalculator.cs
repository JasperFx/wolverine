using System.Diagnostics.Metrics;
using System.Threading.Tasks;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;

namespace Wolverine.RDBMS.Durability;

internal class MetricsCalculator : IDurabilityAction
{
    private readonly ObservableGauge<int> _incoming;
    private readonly ObservableGauge<int> _outgoing;
    private readonly ObservableGauge<int> _scheduled;

    public MetricsCalculator(Meter meter)
    {
        _incoming = meter.CreateObservableGauge(MetricsConstants.InboxCount, () => Counts.Incoming,
            MetricsConstants.Messages, "Inbox messages");
        _outgoing = meter.CreateObservableGauge(MetricsConstants.OutboxCount, () => Counts.Outgoing,
            MetricsConstants.Messages, "Outbox messages");
        _scheduled = meter.CreateObservableGauge(MetricsConstants.ScheduledCount, () => Counts.Scheduled,
            MetricsConstants.Messages, "Scheduled messages");
    }

    public PersistedCounts Counts { get; private set; } = new();

    public string Description { get; } = "Metrics collection of inbox and outbox";

    public async Task ExecuteAsync(IMessageDatabase database, IDurabilityAgent agent,
        IDurableStorageSession session)
    {
        var counts = await database.FetchCountsAsync();
        Counts = counts;
    }
}