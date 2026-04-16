using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Polecat;

namespace Wolverine.Polecat.Subscriptions;

/// <summary>
///     Base class for batched Wolverine subscription
/// </summary>
public abstract class BatchSubscription : IWolverineSubscription, IEventFilterable
{
    private readonly List<Type> _eventTypes = new();
    private Type? _streamType;

    protected BatchSubscription(string subscriptionName)
    {
        SubscriptionName = subscriptionName;
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

    public string SubscriptionName { get; protected set; }

    public uint SubscriptionVersion { get; set; } = 1;

    void IWolverineSubscription.Filter(IEventFilterable filterable)
    {
        foreach (var eventType in _eventTypes) filterable.IncludeType(eventType);

        if (_streamType != null)
        {
            filterable.FilterIncomingEventsOnStreamType(_streamType);
        }

        filterable.IncludeArchivedEvents = IncludeArchivedEvents;
    }

    /// <summary>
    ///     Fine tune how the subscription will be processed at runtime
    /// </summary>
    public AsyncOptions Options { get; } = new();

    /// <summary>
    ///     The actual processing of the events
    /// </summary>
    public abstract Task ProcessEventsAsync(EventRange page, ISubscriptionController controller,
        IDocumentOperations operations, IMessageBus bus,
        CancellationToken cancellationToken);
}
