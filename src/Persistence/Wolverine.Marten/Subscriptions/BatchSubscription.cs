using Marten;
using Marten.Events.Daemon;
using Marten.Events.Daemon.Internals;
using Marten.Events.Projections;

namespace Wolverine.Marten.Subscriptions;

/// <summary>
/// Base class for batched Wolverine subscription
/// </summary>
public abstract class BatchSubscription : IWolverineSubscription, IEventFilterable
{
    private readonly List<Type> _eventTypes = new();
    private Type? _streamType;

    protected BatchSubscription(string subscriptionName)
    {
        SubscriptionName = subscriptionName;
    }

    public string SubscriptionName { get; protected set; }

    public uint SubscriptionVersion { get; set; } = 1;

    void IWolverineSubscription.Filter(IEventFilterable filterable)
    {
        foreach (var eventType in _eventTypes)
        {
            filterable.IncludeType(eventType);
        }

        if (_streamType != null) filterable.FilterIncomingEventsOnStreamType(_streamType);

        filterable.IncludeArchivedEvents = IncludeArchivedEvents;
    }

    public void IncludeType<T>()
    {
        _eventTypes.Add(typeof(T));
    }

    public void IncludeType(Type type)
    {
        _eventTypes.Add(type);
    }

    public void FilterIncomingEventsOnStreamType(Type streamType)
    {
        _streamType = streamType;
    }

    public bool IncludeArchivedEvents { get; set; }

    /// <summary>
    /// Fine tune how the subscription will be processed at runtime
    /// </summary>
    public AsyncOptions Options { get; } = new();

    /// <summary>
    /// The actual processing of the events
    /// </summary>
    /// <param name="page"></param>
    /// <param name="controller"></param>
    /// <param name="operations"></param>
    /// <param name="bus"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task ProcessEventsAsync(EventRange page, ISubscriptionController controller,
        IDocumentOperations operations, IMessageBus bus,
        CancellationToken cancellationToken);
}