using System;
using System.Linq;
using Baseline;
using Baseline.ImTools;
using Wolverine.Attributes;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Sending;

namespace Wolverine.Runtime;

public partial class WolverineRuntime
{
    internal ISendingAgent DetermineLocalSendingAgent(Type messageType)
    {
        if (messageType.HasAttribute<LocalQueueAttribute>())
        {
            var queueName = messageType.GetAttribute<LocalQueueAttribute>()!.QueueName;
            return AgentForLocalQueue(queueName);
        }

        var subscribers = Options.GetOrCreate<LocalTransport>().Endpoints().OfType<LocalQueueSettings>().Where(x => x.ShouldSendMessage(messageType))
            .Select(x => x.Agent)
            .ToArray();

        return subscribers.FirstOrDefault() ?? GetOrBuildSendingAgent(TransportConstants.LocalUri);
    }


    private ImHashMap<Type, IMessageRouter> _messageTypeRouting = ImHashMap<Type, IMessageRouter>.Empty;


    public IMessageRouter RoutingFor(Type messageType)
    {
        if (messageType == typeof(object))
            throw new ArgumentOutOfRangeException(nameof(messageType),
                "System.Object has been erroneously passed in as the message type");

        if (_messageTypeRouting.TryFind(messageType, out var raw))
        {
            return raw;
        }

        var matchingEndpoints = Options.AllEndpoints().Where(x => x.ShouldSendMessage(messageType));
        var conventional = Options.RoutingConventions.SelectMany(x => x.DiscoverSenders(messageType, this));

        var routes = matchingEndpoints.Union(conventional)
            .Select(x => new MessageRoute(messageType, x)).ToArray();

        // If no routes here and this app has a handler for the message type,
        // assume it's a local route
        if (!routes.Any() && Options.HandlerGraph.CanHandle(messageType))
        {
            var localSendingAgentForMessageType = DetermineLocalSendingAgent(messageType);
            var messageRoute = new MessageRoute(messageType, localSendingAgentForMessageType.Endpoint);

            routes = new[] { messageRoute };
        }

        var router = routes.Any()
            ? typeof(MessageRouter<>).CloseAndBuildAs<IMessageRouter>(this, routes, messageType)
            : typeof(EmptyMessageRouter<>).CloseAndBuildAs<IMessageRouter>(this, messageType);

        _messageTypeRouting = _messageTypeRouting.AddOrUpdate(messageType, router);

        return router;
    }
}
