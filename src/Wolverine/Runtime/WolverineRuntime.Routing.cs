using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Sending;

namespace Wolverine.Runtime;

#region sample_IMessageRouteSource

public interface IMessageRouteSource
{
    /// <summary>
    /// Given a message type, what message routes if any can this source find?
    /// </summary>
    /// <param name="messageType"></param>
    /// <param name="runtime"></param>
    /// <returns></returns>
    IEnumerable<IMessageRoute> FindRoutes(Type messageType, IWolverineRuntime runtime);
    
    /// <summary>
    /// If this route source finds matching routes, should Wolverine continue
    /// to add more routes from the subsequent routing sources?
    /// </summary>
    bool IsAdditive { get; }
}

#endregion

internal class AgentMessages : IMessageRouteSource
{
    public IEnumerable<IMessageRoute> FindRoutes(Type messageType, IWolverineRuntime runtime)
    {
        if (messageType.CanBeCastTo<IAgentCommand>())
        {
            var queue = runtime.Endpoints.AgentForLocalQueue(TransportConstants.Agents);
            yield return new MessageRoute(messageType, queue.Endpoint, runtime.Replies);
        }
    }

    public bool IsAdditive => false;
}

internal class ExplicitRouting : IMessageRouteSource
{
    public IEnumerable<IMessageRoute> FindRoutes(Type messageType, IWolverineRuntime runtime)
    {
        return runtime
            .Options
            .Transports
            .AllEndpoints()
            .Where(x => x.ShouldSendMessage(messageType))
            .Select(x => new MessageRoute(messageType, x, runtime.Replies));
    }

    public bool IsAdditive => false;
}

internal class LocalRouting : IMessageRouteSource
{
    public IEnumerable<IMessageRoute> FindRoutes(Type messageType, IWolverineRuntime runtime)
    {
        var options = runtime.Options;
        if (options.LocalRoutingConventionDisabled) return Array.Empty<IMessageRoute>();
        
        if (options.HandlerGraph.CanHandle(messageType))
        {
            var endpoints = options.LocalRouting.DiscoverSenders(messageType, runtime).ToArray();
            return endpoints.Select(e => new MessageRoute(messageType, e, runtime.Replies));
        }

        var batching = options.BatchDefinitions.FirstOrDefault(x => x.ElementType == messageType);
        if (batching == null)
        {
            return Array.Empty<IMessageRoute>();
        }

        var endpoint = options.Transports.GetOrCreate<LocalTransport>()
            .QueueFor(batching.LocalExecutionQueueName);

        return [new MessageRoute(messageType, endpoint, runtime.Replies)];

    }

    public bool IsAdditive { get; set; }
}

internal class MessageRoutingConventions : IMessageRouteSource
{
    public IEnumerable<IMessageRoute> FindRoutes(Type messageType, IWolverineRuntime runtime)
    {
        return runtime.Options.RoutingConventions.SelectMany(x => x.DiscoverSenders(messageType, runtime))
            .Select(e => new MessageRoute(messageType, e, runtime.Replies));
    }

    public bool IsAdditive => true;
}

public partial class WolverineRuntime
{
    private ImHashMap<Type, IMessageRouter> _messageTypeRouting = ImHashMap<Type, IMessageRouter>.Empty;


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

        var routes = findRoutes(messageType);

        var router = routes.Count != 0
            ? typeof(MessageRouter<>).CloseAndBuildAs<IMessageRouter>(this, routes, messageType)
            : typeof(EmptyMessageRouter<>).CloseAndBuildAs<IMessageRouter>(this, messageType);

        Observer.MessageRouted(messageType, router);

        _messageTypeRouting = _messageTypeRouting.AddOrUpdate(messageType, router);

        return router;
    }

    private List<IMessageRoute> findRoutes(Type messageType)
    {
        var routes = new List<IMessageRoute>();
        foreach (var source in Options.RouteSources())
        {
            routes.AddRange(source.FindRoutes(messageType, this));

            if (routes.Count != 0 && !source.IsAdditive) break;
        }

        return routes;
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