using Wolverine.Runtime.Routing;
using Wolverine.Transports.Sending;

namespace Wolverine.Runtime.Sharding;

internal class ShardedMessageRoute : IMessageRoute
{
    private readonly Uri _uri;
    private readonly MessageGroupingRules _grouping;
    private readonly IMessageRoute[] _slots;

    public ShardedMessageRoute(Uri uri, MessageGroupingRules grouping, IMessageRoute[] slots)
    {
        _uri = uri;
        _grouping = grouping;
        _slots = slots;
    }

    public Envelope CreateForSending(object message, DeliveryOptions? options, ISendingAgent localDurableQueue,
        WolverineRuntime runtime, string? topicName)
    {
        var envelope = new Envelope(message);
        options?.Override(envelope);
        var slot = envelope.SlotForSending(_slots.Length, _grouping);
        return _slots[slot].CreateForSending(message, options, localDurableQueue, runtime, topicName);
    }

    public MessageSubscriptionDescriptor Describe()
    {
        // TODO -- there's more to do here!
        return new MessageSubscriptionDescriptor
        {
            Description = "Sharded",
            Endpoint = _uri
        };
    }
}