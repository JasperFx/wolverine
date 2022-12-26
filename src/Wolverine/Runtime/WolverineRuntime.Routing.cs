using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime.Routing;
using Wolverine.Transports.Sending;

namespace Wolverine.Runtime;

public partial class WolverineRuntime
{
    private ImHashMap<Type, IMessageRouter> _messageTypeRouting = ImHashMap<Type, IMessageRouter>.Empty;

    internal IEnumerable<Endpoint> findEndpoints(Type messageType)
    {
        // If there are explicit rules, that's where you go
        var explicits = Options.Transports.AllEndpoints().Where(x => x.ShouldSendMessage(messageType)).ToArray();
        if (explicits.Any()) return explicits;

        if (Options.HandlerGraph.CanHandle(messageType))
        {
            var endpoints = Options.LocalRouting.DiscoverSenders(messageType, this).ToArray();
            if (endpoints.Any()) return endpoints;
        }

        return Options.RoutingConventions.SelectMany(x => x.DiscoverSenders(messageType, this));
    }
    

    public IMessageRouter RoutingFor(Type messageType)
    {
        if (messageType == typeof(object))
        {
            throw new ArgumentOutOfRangeException(nameof(messageType),
                "System.Object has been erroneously passed in as the message type");
        }

        if (_messageTypeRouting.TryFind(messageType, out var raw))
        {
            return raw;
        }

        var routes = findEndpoints(messageType).Select(x => new MessageRoute(messageType, x, Replies))
            .ToArray();
        
        var router = routes.Any()
            ? typeof(MessageRouter<>).CloseAndBuildAs<IMessageRouter>(this, routes, messageType)
            : typeof(EmptyMessageRouter<>).CloseAndBuildAs<IMessageRouter>(this, messageType);

        _messageTypeRouting = _messageTypeRouting.AddOrUpdate(messageType, router);

        return router;
    }

    internal ISendingAgent? DetermineLocalSendingAgent(Type messageType)
    {
        if (Options.LocalRouting.Assignments.TryGetValue(messageType, out var endpoint))
        {
            return endpoint.Agent;
        }

        return null;
    }
}