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
        _incoming = meter.CreateObservableGauge<int>("inbox-count", () => Counts.Incoming, "Messages", "Inboxed messages");
        _outgoing = meter.CreateObservableGauge<int>("outgoing-count", () => Counts.Outgoing, "Messages", "Outboxed messages");
        _scheduled = meter.CreateObservableGauge<int>("scheduled-count", () => Counts.Scheduled, "Messages", "Scheduled messages");
    }
    
    public string Description { get; } = "Metrics collection of inbox and outbox";
    public async Task ExecuteAsync(IEnvelopePersistence storage, IDurabilityAgent agent)
    {
        var counts = await storage.Admin.FetchCountsAsync();
        Counts = counts;
    }

    public PersistedCounts Counts { get; private set; } = new PersistedCounts();
}