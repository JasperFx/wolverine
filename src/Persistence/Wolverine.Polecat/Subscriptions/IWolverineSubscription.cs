using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Polecat;

namespace Wolverine.Polecat.Subscriptions;

/// <summary>
/// Interface for plugging in custom Wolverine subscriptions to Polecat event data
/// </summary>
public interface IWolverineSubscription
{
    /// <summary>
    /// Identification within Polecat
    /// </summary>
    public string SubscriptionName { get; }

    /// <summary>
    /// Apply versioning if you need blue/green subscriptions for new versions to catch up from the beginning
    /// </summary>
    public uint SubscriptionVersion { get; set; }

    /// <summary>
    /// Apply filters on event data for better runtime efficiency
    /// </summary>
    /// <param name="filterable"></param>
    void Filter(IEventFilterable filterable);

    /// <summary>
    /// Fine tune the behavior of this subscription within Polecat's "async daemon"
    /// </summary>
    public AsyncOptions Options { get; }

    /// <summary>
    /// The actual hook to process pages of events. The Polecat async daemon will call this for you
    /// </summary>
    /// <param name="page">The current page of events in sequential order</param>
    /// <param name="controller"></param>
    /// <param name="operations">Access to Polecat queries and writes</param>
    /// <param name="bus">The current Wolverine message bus to raise messages or execute messages inline</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task ProcessEventsAsync(EventRange page, ISubscriptionController controller, IDocumentOperations operations,
        IMessageBus bus, CancellationToken cancellationToken);
}
