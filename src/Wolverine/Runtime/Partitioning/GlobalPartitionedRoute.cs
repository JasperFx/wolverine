using Wolverine.Configuration;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Runtime.Partitioning;

internal class GlobalPartitionedRoute : IMessageRoute
{
    private readonly Uri _uri;
    private readonly MessagePartitioningRules _partitioning;
    private readonly IMessageRoute[] _externalSlots;
    private readonly IMessageRoute[] _localSlots;
    private readonly Endpoint[] _externalEndpoints;
    private readonly bool _nativeAcks;

    /// <summary>
    /// The set of local queue URIs that sticky handler fanout will deliver to.
    /// Used by MessageRouter to deduplicate explicit routes to these same queues.
    /// See https://github.com/JasperFx/wolverine/issues/2303
    /// </summary>
    internal HashSet<Uri> StickyHandlerFanoutUris { get; } = new();

    public GlobalPartitionedRoute(Uri uri, MessagePartitioningRules partitioning,
        IMessageRoute[] externalSlots, IMessageRoute[] localSlots, Endpoint[] externalEndpoints,
        bool nativeAcks = false)
    {
        _uri = uri;
        _partitioning = partitioning;
        _externalSlots = externalSlots;
        _localSlots = localSlots;
        _externalEndpoints = externalEndpoints;
        _nativeAcks = nativeAcks;
    }

    public Envelope CreateForSending(object message, DeliveryOptions? options, ISendingAgent localDurableQueue,
        WolverineRuntime runtime, string? topicName)
    {
        var envelope = new Envelope(message);
        options?.Override(envelope);
        var slot = envelope.SlotForSending(_externalSlots.Length, _partitioning);

        // GH-3709. The local shortcut hands the message straight to the companion local queue when this
        // node already owns the slot, skipping the broker. A native-ack topology has no companion queue
        // to hand it to -- and more to the point, the broker delivery IS the durability story in that
        // mode, so short-circuiting it would drop the message on a crash between send and handling.
        // Always go through the broker.
        // GH-4673. The shortcut is only safe when the message is handled NOW. A scheduled message is
        // handled later -- seconds or hours -- and slot ownership at send time says nothing about who owns
        // the slot when it comes due. Taking the shortcut parks it in the inbox at the companion local
        // queue's address, and that address exists on EVERY node (LocalQueue.IsSingleNodeListener is
        // deliberately false, GH-3856), so whichever node's scheduled poller wins the advisory lock when it
        // comes due executes it -- concurrently with the real slot owner, under the same group id, which
        // is the one thing global partitioning is there to prevent.
        //
        // Parking it at the external slot instead makes ownership a question asked when it is due rather
        // than when it is sent. The owning node's listener picks it up; a non-owner forwards it to the
        // slot through the GH-4645 path. Both answers are correct whoever polls.
        if (!_nativeAcks && !envelope.IsScheduledForLater(DateTimeOffset.UtcNow))
        {
            // Check if this slot's exclusive listener is active on the current node
            var externalEndpoint = _externalEndpoints[slot];
            var listeningAgent = runtime.Endpoints.FindListeningAgent(externalEndpoint.Uri);

            if (listeningAgent != null && listeningAgent.Status == ListeningStatus.Accepting)
            {
                // Local shortcut: route directly to the companion local queue
                return _localSlots[slot].CreateForSending(message, options, localDurableQueue, runtime, topicName);
            }
        }

        // Remote: route through the external transport
        return _externalSlots[slot].CreateForSending(message, options, localDurableQueue, runtime, topicName);
    }

    public MessageSubscriptionDescriptor Describe()
    {
        return new MessageSubscriptionDescriptor
        {
            Description = "Global Partitioned",
            Endpoint = _uri,
            Partitions = _externalSlots.Select(x => x.Describe()).ToArray()
        };
    }
}
