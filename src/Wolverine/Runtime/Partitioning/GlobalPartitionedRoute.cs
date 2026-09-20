using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Sending;

namespace Wolverine.Runtime.Partitioning;

internal class GlobalPartitionedRoute : IMessageRoute, IMessageInvoker
{
    private readonly Uri _uri;
    private readonly MessagePartitioningRules _partitioning;
    private readonly IMessageRoute[] _externalSlots;
    private readonly IMessageRoute[] _localSlots;
    private readonly Endpoint[] _externalEndpoints;
    private readonly Endpoint[] _localEndpoints;
    private readonly bool _nativeAcks;

    // Both the external slot and its companion local queue map to the shared slot index: a broker-delivered
    // envelope names the external listener in Destination (MarkReceived stamps whichever listener delivered
    // it) but executes on the companion queue's lanes, where GlobalPartitionedReceiverBridge forwards it.
    private readonly ImHashMap<Uri, int> _slotsByUri;

    /// <summary>
    /// The set of local queue URIs that sticky handler fanout will deliver to.
    /// Used by MessageRouter to deduplicate explicit routes to these same queues.
    /// See https://github.com/JasperFx/wolverine/issues/2303
    /// </summary>
    internal HashSet<Uri> StickyHandlerFanoutUris { get; } = new();

    public GlobalPartitionedRoute(Uri uri, MessagePartitioningRules partitioning,
        IMessageRoute[] externalSlots, IMessageRoute[] localSlots, Endpoint[] externalEndpoints,
        bool nativeAcks = false, Endpoint[]? localEndpoints = null)
    {
        _uri = uri;
        _partitioning = partitioning;
        _externalSlots = externalSlots;
        _localSlots = localSlots;
        _externalEndpoints = externalEndpoints;
        _localEndpoints = localEndpoints ?? [];
        _nativeAcks = nativeAcks;

        var slotsByUri = ImHashMap<Uri, int>.Empty;
        for (var i = 0; i < _externalEndpoints.Length; i++)
        {
            slotsByUri = slotsByUri.AddOrUpdate(_externalEndpoints[i].Uri, i);
        }

        for (var i = 0; i < _localEndpoints.Length; i++)
        {
            slotsByUri = slotsByUri.AddOrUpdate(_localEndpoints[i].Uri, i);
        }

        _slotsByUri = slotsByUri;
    }

    public Envelope CreateForSending(object message, DeliveryOptions? options, ISendingAgent localDurableQueue,
        WolverineRuntime runtime, string? topicName)
    {
        var (slot, _) = selectSlot(message, options);

        if (ownsSlotLocally(slot, runtime))
        {
            // Local shortcut: route directly to the companion local queue
            return _localSlots[slot].CreateForSending(message, options, localDurableQueue, runtime, topicName);
        }

        // Remote: route through the external transport
        return _externalSlots[slot].CreateForSending(message, options, localDurableQueue, runtime, topicName);
    }

    // Shared by the publishing and request/reply paths so the two can never disagree about a group's shard
    private (int Slot, string? GroupId) selectSlot(object message, DeliveryOptions? options)
    {
        var envelope = new Envelope(message);
        options?.Override(envelope);
        var slot = envelope.SlotForSending(_externalSlots.Length, _partitioning);
        return (slot, envelope.GroupId);
    }

    private bool ownsSlotLocally(int slot, IWolverineRuntime runtime)
    {
        // GH-3709. The local shortcut hands the message straight to the companion local queue when this
        // node already owns the slot, skipping the broker. A native-ack topology has no companion queue
        // to hand it to -- and more to the point, the broker delivery IS the durability story in that
        // mode, so short-circuiting it would drop the message on a crash between send and handling.
        // Always go through the broker.
        if (_nativeAcks)
        {
            return false;
        }

        var listeningAgent = runtime.Endpoints.FindListeningAgent(_externalEndpoints[slot].Uri);
        return listeningAgent is { Status: ListeningStatus.Accepting };
    }

    public Task<T> InvokeAsync<T>(object message, MessageBus bus, CancellationToken cancellation = default,
        TimeSpan? timeout = null, DeliveryOptions? options = null)
    {
        var selection = selectForInvocation(message, options, bus);
        assertNotDirectReentry(message, bus, selection);

        return selection.Route.RemoteInvokeAsync<T>(message, bus, cancellation, timeout, options,
            groupId: selection.GroupId);
    }

    public Task InvokeAsync(object message, MessageBus bus, CancellationToken cancellation = default,
        TimeSpan? timeout = null, DeliveryOptions? options = null)
    {
        return InvokeAsync<Acknowledgement>(message, bus, cancellation, timeout, options);
    }

    public IAsyncEnumerable<T> StreamAsync<T>(object message, MessageBus bus,
        CancellationToken cancellation = default,
        DeliveryOptions? options = null)
    {
        throw new NotSupportedException(
            $"Streaming is only supported for locally handled messages. {message.GetType().FullNameInCode()} is routed through the globally partitioned topology at {_uri}.");
    }

    private readonly record struct InvocationTarget(MessageRoute Route, Endpoint Endpoint, int Slot,
        string? GroupId);

    private InvocationTarget selectForInvocation(object message, DeliveryOptions? options, MessageBus bus)
    {
        if (_nativeAcks)
        {
            throw new NotSupportedException(
                $"Awaiting a reply through a globally partitioned topology is not supported when the topology uses {nameof(GlobalPartitionedMessageTopology.ProcessInParallelWithNativeAcks)}(). "
                + $"The topology at {_uri} has no companion local queues, and request/reply over its native-ack slots is not yet validated. Use the default durable topology, or publish without awaiting a reply.");
        }

        var (slot, groupId) = selectSlot(message, options);
        var local = ownsSlotLocally(slot, bus.Runtime);

        var selected = local ? _localSlots[slot] : _externalSlots[slot];
        var endpoint = local ? _localEndpoints[slot] : _externalEndpoints[slot];

        if (selected is not MessageRoute route)
        {
            throw new InvalidOperationException(
                $"Slot {slot} of the globally partitioned topology at {_uri} is a {selected.GetType().Name}, which does not support request/reply invocation.");
        }

        return new InvocationTarget(route, endpoint, slot, groupId);
    }

    // Two different group ids can share a lane (|hash(groupId)| % laneCount within a slot), so re-entry is
    // detected on the resolved lane rather than on group id equality. Only DIRECT re-entry is caught; an
    // indirect cycle across two lanes still surfaces as a timeout.
    private void assertNotDirectReentry(object message, MessageBus bus, InvocationTarget target)
    {
        var groupId = target.GroupId;

        // Only an in-process target can deadlock the caller; a broker-bound one runs on the node owning it
        if (target.Endpoint is not LocalQueue queue)
        {
            return;
        }

        var inbound = bus.Envelope;

        if (inbound?.Destination == null || inbound.Message == null)
        {
            return;
        }

        if (!_slotsByUri.TryFind(inbound.Destination, out var inboundSlot) || inboundSlot != target.Slot)
        {
            return;
        }

        if (queue.GroupShardingSlotNumber is not { } slots)
        {
            return;
        }

        // SlotForProcessing picks a random lane for an empty group id, and a guess is no grounds for refusing
        if (groupId.IsEmpty() || inbound.GroupId.IsEmpty())
        {
            return;
        }

        // GH-3899: an exempted type runs on the shared parallel lane, so there is no head-of-line dependency
        if (_partitioning.IsExemptFromPartitionedProcessing(inbound.Message.GetType())
            || _partitioning.IsExemptFromPartitionedProcessing(message.GetType()))
        {
            return;
        }

        var laneCount = (int)slots;
        var outgoingLane = new Envelope(message) { GroupId = groupId }.SlotForProcessing(laneCount, _partitioning);
        var inboundLane = inbound.SlotForProcessing(laneCount, _partitioning);

        if (outgoingLane != inboundLane)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Cannot await a reply for {message.GetType().FullNameInCode()} from a handler that is already executing on the same partitioned lane. "
            + $"Group id '{inbound.GroupId}' (inbound) and '{groupId}' (outgoing) both map to lane {inboundLane} of {laneCount} on slot {target.Slot} at {queue.Uri}, "
            + "and a lane executes one message at a time -- the invoked message could not start until this handler returned. "
            + "Publish the message instead of awaiting it, or move the awaited work outside of this handler.");
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
