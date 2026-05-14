using System.Diagnostics.CodeAnalysis;
using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Sending;

namespace Wolverine.Runtime;

#region sample_imessageroutesource
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

/// <summary>
/// Optional interface for IMessageRouteSource implementations to expose their
/// target endpoints, enabling endpoint policies (like UseDurableOutboxOnAllSendingEndpoints)
/// to discover and configure them.
/// </summary>
public interface IEndpointSource
{
    IEnumerable<Endpoint> ActiveEndpoints();
}

internal class AgentMessages : IMessageRouteSource
{
    public IEnumerable<IMessageRoute> FindRoutes(Type messageType, IWolverineRuntime runtime)
    {
        if (messageType.CanBeCastTo<IAgentCommand>())
        {
            var queue = runtime.Endpoints.AgentForLocalQueue(TransportConstants.Agents);
            yield return new MessageRoute(messageType, queue.Endpoint, runtime);
        }
    }

    public bool IsAdditive => false;
}

internal class ExplicitRouting : IMessageRouteSource
{
    public IEnumerable<IMessageRoute> FindRoutes(Type messageType, IWolverineRuntime runtime)
    {
        var explicitRoutes = runtime
            .Options
            .Transports
            .AllEndpoints()
            .Where(x => x.ShouldSendMessage(messageType))
            .Select(x => new MessageRoute(messageType, x, runtime));

        foreach (var explicitRoute in explicitRoutes)
        {
            yield return explicitRoute;
        }

        foreach (var topology in runtime.Options.MessagePartitioning.ShardedMessageTopologies)
        {
            if (topology.TryMatch(messageType, runtime, out var route))
            {
                yield return route!;
            }
        }

        foreach (var topology in runtime.Options.MessagePartitioning.GlobalPartitionedTopologies)
        {
            if (topology.TryMatch(messageType, runtime, out var globalRoute))
            {
                yield return globalRoute!;
            }
        }
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
            return endpoints.Select(e => new MessageRoute(messageType, e, runtime));
        }

        var batching = options.BatchDefinitions.FirstOrDefault(x => x.ElementType == messageType);
        if (batching == null)
        {
            return [];
        }

        var endpoint = options.Transports.GetOrCreate<LocalTransport>()
            .QueueFor(batching.LocalExecutionQueueName!);

        return [new MessageRoute(messageType, endpoint, runtime)];

    }

    public bool IsAdditive { get; set; }
}

internal class MessageRoutingConventions : IMessageRouteSource
{
    public IEnumerable<IMessageRoute> FindRoutes(Type messageType, IWolverineRuntime runtime)
    {
        return runtime.Options.RoutingConventions.SelectMany(x => x.DiscoverSenders(messageType, runtime))
            .Select(e => new MessageRoute(messageType, e, runtime));
    }

    public bool IsAdditive => true;
}

public partial class WolverineRuntime
{
    private ImHashMap<Type, IMessageRouter> _messageTypeRouting = ImHashMap<Type, IMessageRouter>.Empty;


    // RoutingFor is the per-message-type router-resolution entry point. The cache-
    // miss path closes MessageRouter<T> / EmptyMessageRouter<T> over the runtime-
    // resolved messageType — same CloseAndBuildAs reflective pattern as chunk D
    // and chunk I. AOT-clean apps in TypeLoadMode.Static pre-populate this cache
    // at bootstrap (envelope-mapper / message-type discovery sources, see #2715
    // and the AOT publishing guide) so the steady-state hot path is pure lookups
    // and the miss path's reflective close never fires. Trim-only apps without
    // source generation need MessageRouter<>/EmptyMessageRouter<> closed types
    // preserved via TrimmerRootDescriptor on their message types.
    //
    // Leaf suppression rather than [RequiresDynamicCode] on RoutingFor because
    // RoutingFor is on the per-message dispatch hot path through MessageContext.
    // Cascading [Requires*] up there would force every user-facing send/publish
    // API to declare it.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Closed generic resolved from runtime messageType; AOT consumers pre-populate _messageTypeRouting via source-generated discovery. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Closed generic resolved from runtime messageType; AOT consumers pre-populate _messageTypeRouting via source-generated discovery. See AOT guide.")]
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

        // Skip framework-internal types (IAgentCommand, INotToBeRouted, IInternalMessage,
        // and types from assemblies marked [ExcludeFromServiceCapabilities]) so they
        // never reach observers like CritterWatch. See GH-2520.
        if (!messageType.IsSystemMessageType())
        {
            Observer.MessageRouted(messageType, router);
        }

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

    /// <summary>
    /// Pre-populate the per-message-type router cache with the supplied message
    /// types. Called from <see cref="WolverineRuntime.HostService.StartAsync"/>
    /// after the handler graph has compiled and the route sources are wired up,
    /// so the per-message <see cref="RoutingFor"/> hot path never pays the
    /// first-occurrence reflection cost (CloseAndBuildAs over MessageRouter&lt;T&gt;
    /// / EmptyMessageRouter&lt;T&gt;). Closes the AOT story for the per-message
    /// router resolution from AOT pillar issue #2769.
    /// </summary>
    /// <remarks>
    /// Tolerates duplicates and the typeof(object) sentinel; <see cref="RoutingFor"/>
    /// itself filters those. <see cref="System.IsSystemMessageType"/>-flagged types
    /// are still visited so framework-internal routers get cached too — the
    /// cache lookup is the same dictionary either way.
    /// </remarks>
    /// <param name="messageTypes">Message types to resolve and cache.</param>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Pre-populating the message-router cache at host startup; same suppression as RoutingFor. See AOT guide / #2769.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Pre-populating the message-router cache at host startup; same suppression as RoutingFor. See AOT guide / #2769.")]
    internal void PrepopulateRoutingCache(IEnumerable<Type>? messageTypes)
    {
        if (messageTypes == null) return;

        foreach (var messageType in messageTypes)
        {
            if (messageType == null) continue;
            if (messageType == typeof(object)) continue;
            if (_messageTypeRouting.TryFind(messageType, out _)) continue;

            // RoutingFor populates _messageTypeRouting as a side effect of the
            // cache-miss path. Just drop the result.
            _ = RoutingFor(messageType);
        }
    }

    internal ISendingAgent? DetermineLocalSendingAgent(Type messageType)
    {
        if (Options.LocalRouting.Assignments.TryGetValue(messageType, out var endpoint))
        {
            return endpoint.Agent;
        }

        return null;
    }

    internal void ClearRoutingFor(Type messageType)
    {
        _messageTypeRouting = _messageTypeRouting.Remove(messageType);
    }
}