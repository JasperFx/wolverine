using System.Diagnostics;
using JasperFx.Core;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;

namespace Wolverine.Runtime;

public partial class MessageBus : IBroadcastToAllNodes
{
    private static readonly Uri DefaultLocalQueueUri = $"{TransportConstants.Local}://{TransportConstants.Default}".ToUri();

    async ValueTask IBroadcastToAllNodes.BroadcastToAllNodesAsync(object message, DeliveryOptions? options)
    {
        ArgumentNullException.ThrowIfNull(message);

        Runtime.AssertHasStarted();
        assertNotMediatorOnly();
        options = applyTenantContext(options);

        var messageType = message.GetType();
        var router = Runtime.RoutingFor(messageType);

        var outgoing = new List<Envelope>();
        outgoing.AddRange(routeToThisNode(router, message, messageType, options));

        if (Runtime.Options.Durability.Mode == DurabilityMode.Balanced)
        {
            foreach (var controlUri in await findPeerControlUrisAsync().ConfigureAwait(false))
            {
                outgoing.Add(router.RouteToDestination(message, controlUri, options));
            }
        }

        if (outgoing.Count == 0)
        {
            Runtime.MessageTracking.NoRoutesFor(new Envelope(message));
            return;
        }

        var envelopes = outgoing.ToArray();

        // Every send path has to stamp correlation itself. GH-4556 was BroadcastToTopicAsync skipping
        // this and shipping envelopes with no Source, CorrelationId or ParentId.
        trackEnvelopeCorrelation(Activity.Current, envelopes);

        await PersistOrSendAsync(envelopes).ConfigureAwait(false);
    }

    private Envelope[] routeToThisNode(IMessageRouter router, object message, Type messageType,
        DeliveryOptions? options)
    {
        // Only the LOCAL routes the message type normally has, so this node handles the message the way it
        // always does locally (its local queue's mode, separated handlers each getting their own copy), but
        // never also sends it on to an external endpoint. Peers receiving it on their control endpoint
        // don't forward it anywhere either.
        var local = router.RouteForPublish(message, options)
            .Where(x => x.Destination?.Scheme == TransportConstants.Local)
            .ToArray();

        if (local.Length > 0) return local;

        // A message type explicitly routed to an external endpoint has no local route at all, but this
        // node can still handle it.
        if (Runtime.Options.HandlerGraph.CanHandle(messageType))
        {
            return [router.RouteToDestination(message, DefaultLocalQueueUri, options)];
        }

        return [];
    }

    private async Task<Uri[]> findPeerControlUrisAsync()
    {
        // Node records always live in the main message store, even when this bus is using an ancillary one
        var nodes = await Runtime.Storage.Nodes.LoadAllNodesAsync(Runtime.Cancellation).ConfigureAwait(false);

        var staleTime = DateTimeOffset.UtcNow.Subtract(Runtime.Options.Durability.StaleNodeTimeout);
        return SelectPeerControlUris(nodes, Runtime.Options.UniqueNodeId, staleTime);
    }

    internal static Uri[] SelectPeerControlUris(IEnumerable<WolverineNode> nodes, Guid self, DateTimeOffset staleTime)
    {
        // Same staleness rule the leader applies before it ejects a node. A node that stopped
        // heart-beating may be gone, and a message sent to its control endpoint would never be handled.
        return nodes
            .Where(x => x.NodeId != self && x.ControlUri != null && x.LastHealthCheck >= staleTime)
            .Select(x => x.ControlUri!)
            .ToArray();
    }
}
