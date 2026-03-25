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

    /// <summary>
    /// The set of local queue URIs that sticky handler fanout will deliver to.
    /// Used by MessageRouter to deduplicate explicit routes to these same queues.
    /// See https://github.com/JasperFx/wolverine/issues/2303
    /// </summary>
    internal HashSet<Uri> StickyHandlerFanoutUris { get; } = new();

    public GlobalPartitionedRoute(Uri uri, MessagePartitioningRules partitioning,
        IMessageRoute[] externalSlots, IMessageRoute[] localSlots, Endpoint[] externalEndpoints)
    {
        _uri = uri;
        _partitioning = partitioning;
        _externalSlots = externalSlots;
        _localSlots = localSlots;
        _externalEndpoints = externalEndpoints;
    }

    public Envelope CreateForSending(object message, DeliveryOptions? options, ISendingAgent localDurableQueue,
        WolverineRuntime runtime, string? topicName)
    {
        var envelope = new Envelope(message);
        options?.Override(envelope);
        var slot = envelope.SlotForSending(_externalSlots.Length, _partitioning);

        // Check if this slot's exclusive listener is active on the current node
        var externalEndpoint = _externalEndpoints[slot];
        var listeningAgent = runtime.Endpoints.FindListeningAgent(externalEndpoint.Uri);

        if (listeningAgent != null && listeningAgent.Status == ListeningStatus.Accepting)
        {
            // Local shortcut: route directly to the companion local queue
            return _localSlots[slot].CreateForSending(message, options, localDurableQueue, runtime, topicName);
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
