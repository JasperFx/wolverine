using JasperFx.Core;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Events;
using Marten.Events.Daemon;
using Marten.Events.Daemon.Internals;

namespace Wolverine.Marten.Subscriptions;

public interface IPublishingRelay
{
    /// <summary>
    /// If any publishing rules are established, this creates an "allow list" of event types
    /// to subscribe to. This usage will do a transformation of the IEvent<T> to sending messages
    /// via Wolverine in the publish lambda
    /// </summary>
    /// <param name="publish"></param>
    /// <typeparam name="T"></typeparam>
    void PublishEvent<T>(Func<IEvent<T>, IMessageBus, ValueTask> publish) where T : notnull;

    /// <summary>
    /// Forward events of this type to Wolverine. This creates an allow list of event types
    /// if any specific event types are specified
    /// </summary>
    /// <typeparam name="T"></typeparam>
    void PublishEvent<T>();

    /// <summary>
    /// Forward events of this type to Wolverine. This creates an allow list of event types
    /// if any specific event types are specified
    /// </summary>
    /// <param name="eventType"></param>
    void PublishEvent(Type eventType);

    uint SubscriptionVersion { get; set; }

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
    /// <param name="streamType"></param>
    void FilterIncomingEventsOnStreamType(Type streamType);
}

internal class PublishingRelay : BatchSubscription, IPublishingRelay
{
    private ImHashMap<Type, IPublisher> _publishers = ImHashMap<Type, IPublisher>.Empty;

    public PublishingRelay(string subscriptionName) : base(subscriptionName)
    {
    }

    public void PublishEvent<T>(Func<IEvent<T>, IMessageBus, ValueTask> publish) where T : notnull
    {
        IncludeType<T>();
        var publisher = typeof(LambdaPublisher<>).CloseAndBuildAs<IPublisher>( publish, typeof(T));
        _publishers = _publishers.AddOrUpdate(typeof(T), publisher);
    }

    public void PublishEvent<T>()
    {
        IncludeType<T>();
    }

    public void PublishEvent(Type eventType)
    {
        IncludeType(eventType);
    }

    public override async Task ProcessEventsAsync(EventRange page, ISubscriptionController controller,
        IDocumentOperations operations, IMessageBus bus,
        CancellationToken cancellationToken)
    {
        foreach (var e in page.Events)
        {
            if (_publishers.TryFind(e.EventType, out var publisher))
            {
                await publisher.PublishAsync(e, bus);
            }
            else
            {
                await bus.PublishAsync(e);
            }
        }
    }

    internal interface IPublisher
    {
        ValueTask PublishAsync(object e, IMessageBus bus);
    }

    internal class LambdaPublisher<T> : IPublisher where T : notnull
    {
        private readonly Func<IEvent<T>, IMessageBus, ValueTask> _publish;

        public LambdaPublisher(Func<IEvent<T>, IMessageBus, ValueTask> publish)
        {
            _publish = publish;
        }

        public ValueTask PublishAsync(object e, IMessageBus bus)
        {
            if (e is IEvent<T> typed) return _publish(typed, bus);

            return new ValueTask();
        }
    }
}