using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Polecat;

using Wolverine.Persistence.EventSourcing.Subscriptions;

namespace Wolverine.Polecat.Subscriptions;

public interface IPublishingRelay
{
    /// <summary>
    /// If any publishing rules are established, this creates an "allow list" of event types
    /// to subscribe to. This usage will do a transformation of the IEvent&lt;T&gt; to sending messages
    /// via Wolverine in the publish lambda
    /// </summary>
    void PublishEvent<T>(Func<IEvent<T>, IMessageBus, ValueTask> publish) where T : notnull;

    /// <summary>
    /// Forward events of this type to Wolverine. This creates an allow list of event types
    /// if any specific event types are specified
    /// </summary>
    void PublishEvent<T>();

    /// <summary>
    /// Forward events of this type to Wolverine. This creates an allow list of event types
    /// if any specific event types are specified
    /// </summary>
    void PublishEvent(Type eventType);

    uint Version { get; set; }

    /// <summary>
    /// Should this subscription be applied to archived events? The default is false
    /// </summary>
    bool IncludeArchivedEvents { get; set; }

    /// <summary>
    /// Fine tune how the subscription will be processed at runtime
    /// </summary>
    AsyncOptions Options { get; }

    /// <summary>
    /// Only subscribe to streams tagged with this stream type
    /// </summary>
    void FilterIncomingEventsOnStreamType(Type streamType);
}

/// <summary>
/// The Polecat subscription behind PublishEventsToWolverine(). The behavior is the shared
/// <see cref="EventRelay"/> (GH-5143).
/// </summary>
internal class PublishingRelay : BatchSubscription, IPublishingRelay
{
    private readonly EventRelay _relay;

    public PublishingRelay(string subscriptionName) : base(subscriptionName)
    {
        _relay = new EventRelay(false);
    }

    public void PublishEvent<T>(Func<IEvent<T>, IMessageBus, ValueTask> publish) where T : notnull
    {
        IncludeType<T>();
        _relay.PublishEvent(publish);
    }

    public void PublishEvent<T>()
    {
        IncludeType<T>();
    }

    public void PublishEvent(Type eventType)
    {
        IncludeType(eventType);
    }

    public override Task ProcessEventsAsync(EventRange page, ISubscriptionController controller,
        IDocumentOperations operations, IMessageBus bus,
        CancellationToken cancellationToken)
    {
        return _relay.RelayAsync(page.Events, bus);
    }
}
