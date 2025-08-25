using Wolverine.Runtime.Routing;
using Wolverine.Transports.Sending;

namespace Wolverine.Runtime.Sharding;

internal class ShardedMessageRoute : IMessageRoute
{
    private readonly Uri _uri;
    private readonly MessagePartitioningRules _partitioning;
    private readonly IMessageRoute[] _slots;

    public ShardedMessageRoute(Uri uri, MessagePartitioningRules partitioning, IMessageRoute[] slots)
    {
        _uri = uri;
        _partitioning = partitioning;
        _slots = slots;
    }

    public Envelope CreateForSending(object message, DeliveryOptions? options, ISendingAgent localDurableQueue,
        WolverineRuntime runtime, string? topicName)
    {
        var envelope = new Envelope(message);
        options?.Override(envelope);
        var slot = envelope.SlotForSending(_slots.Length, _partitioning);
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