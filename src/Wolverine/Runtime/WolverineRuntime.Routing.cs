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

    /// <summary>
    /// Diagnostic description of this route source for routing explanations, the
    /// describe-routing CLI, and service capabilities. The default implementation reports the
    /// type name and IsAdditive with no description; built-in and extension sources should
    /// override to supply a meaningful, AI-readable explanation (and any conventions consulted).
    /// </summary>
    RouteSourceDescriptor Describe(IWolverineRuntime runtime) => new()
    {
        Name = GetType().Name,
        Description = string.Empty,
        IsAdditive = IsAdditive
    };
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
            yield return MessageRoute.For(messageType, queue.Endpoint, runtime);
        }
    }

    public bool IsAdditive => false;

    public RouteSourceDescriptor Describe(IWolverineRuntime runtime) => new()
    {
        Name = "AgentCommands",
        Description = "Routes Wolverine agent command messages (types implementing IAgentCommand) to the internal agent local queue. Terminating: nothing else routes an agent command.",
        IsAdditive = IsAdditive
    };
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
            .Select(x => MessageRoute.For(messageType, x, runtime));

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

    public RouteSourceDescriptor Describe(IWolverineRuntime runtime) => new()
    {
        Name = "ExplicitRouting",
        Description = "Routes to endpoints with explicit publishing rules (PublishMessage/PublishAllMessages/To...) that match this message type, plus any sharded or globally-partitioned message topologies. Terminating: when an explicit rule matches, conventional and local routing are skipped.",
        IsAdditive = IsAdditive
    };
}

internal class LocalRouting : IMessageRouteSource
{
    public IEnumerable<IMessageRoute> FindRoutes(Type messageType, IWolverineRuntime runtime)
    {
        var options = runtime.Options;
        if (options.LocalRoutingConventionDisabled) return Array.Empty<IMessageRoute>();
        
        if (options.HandlerGraph.CanHandle(messageType))
        {
            var endpoints = options.LocalRouting.DiscoverSenders(messageType, runtime).ToList();

            // Under MultipleHandlerBehavior.Separated, an element type may have BOTH a direct
            // handler (covered above) AND a BatchMessagesOf<T>() batch handler living on its own
            // dedicated queue. Fan the element type out to the batch queue as well so the batch
            // handler runs independently of the direct handler.
            var batchEndpoint = FindSeparatedBatchEndpoint(messageType, options);
            if (batchEndpoint != null && endpoints.All(e => !Equals(e.Uri, batchEndpoint.Uri)))
            {
                endpoints.Add(batchEndpoint);
            }

            return endpoints.Select(e => MessageRoute.For(messageType, e, runtime));
        }

        var batching = options.BatchDefinitions.FirstOrDefault(x => x.ElementType == messageType);
        if (batching == null)
        {
            return [];
        }

        var endpoint = options.Transports.GetOrCreate<LocalTransport>()
            .QueueFor(batching.LocalExecutionQueueName!);

        return [MessageRoute.For(messageType, endpoint, runtime)];

    }

    /// <summary>
    /// Under <see cref="MultipleHandlerBehavior.Separated"/>, an element type that has a direct
    /// handler may ALSO have a <c>BatchMessagesOf&lt;T&gt;()</c> batch handler living on its own
    /// dedicated local queue. Returns that batch queue endpoint (if any) so the element type can be
    /// fanned out to it in addition to the direct handler's queue. Returns null when not in
    /// Separated mode or when the element type has no batch definition.
    /// </summary>
    private static LocalQueue? FindSeparatedBatchEndpoint(Type messageType, WolverineOptions options)
    {
        if (options.MultipleHandlerBehavior != MultipleHandlerBehavior.Separated)
        {
            return null;
        }

        var batchDefinition = options.BatchDefinitions.FirstOrDefault(x => x.ElementType == messageType);
        if (batchDefinition?.LocalExecutionQueueName is { } batchQueueName)
        {
            return options.Transports.GetOrCreate<LocalTransport>().QueueFor(batchQueueName);
        }

        return null;
    }

    public bool IsAdditive { get; set; }

    public RouteSourceDescriptor Describe(IWolverineRuntime runtime) => new()
    {
        Name = "LocalRouting",
        Description = "Routes a message to a local in-process queue when the application has a handler for it (or a registered message batch). This is how commands handled in the same process are dispatched. Disabled by LocalRoutingConventionDisabled.",
        IsAdditive = IsAdditive
    };
}

internal class MessageRoutingConventions : IMessageRouteSource
{
    public IEnumerable<IMessageRoute> FindRoutes(Type messageType, IWolverineRuntime runtime)
    {
        return runtime.Options.RoutingConventions.SelectMany(x => x.DiscoverSenders(messageType, runtime))
            .Select(e => MessageRoute.For(messageType, e, runtime));
    }

    public bool IsAdditive => true;

    public RouteSourceDescriptor Describe(IWolverineRuntime runtime) => new()
    {
        Name = "ConventionalRouting",
        Description = "Routes via registered message routing conventions — including broker conventions (e.g. RabbitMQ/Kafka topic-or-queue-per-message-type) configured with UseConventionalRouting. Additive: convention routes are combined with any others.",
        IsAdditive = IsAdditive,
        Conventions = runtime.Options.RoutingConventions.Select(x => x.Describe(runtime)).ToArray()
    };
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
        //
        // Also skip the observer call during "description" mode. WolverineSystemPart.
        // FindResources() walks every discovered message type through RoutingFor to
        // surface IStatefulResource instances for transports; while that pass runs,
        // WithinDescription is true and MessageRoute is allowed to take a null Sender
        // and a null Serializer (the endpoint may not have a DefaultSerializer assigned
        // until transports finish initializing). Routes in that degraded state are not
        // safe to hand to observers — calling MessageRoute.Describe() on them NREs on
        // Serializer.ContentType, killing the host at resource-setup-on-startup.
        // Observers re-fire from the real RoutingFor calls once the runtime is live,
        // so suppressing during description loses no signal. See GH-3088.
        if (!messageType.IsSystemMessageType() && !WolverineSystemPart.WithinDescription)
        {
            Observer.MessageRouted(messageType, router);
        }

        // Never cache routes built during "description" mode. While
        // WolverineSystemPart.WithinDescription is true, MessageRoute is allowed to take a
        // null Sender (the endpoint's sending agent may not be built yet — e.g. during
        // FindResources()/resource-setup-on-startup, before transports start). Caching such a
        // route would let a null Sender escape onto the runtime hot path and NRE inside
        // CreateForSending (the Envelope ctor dereferences agent.Endpoint). Routes requested
        // during description are display-only and ephemeral; the real cache is populated later
        // with live agents. See GH-2897.
        if (!WolverineSystemPart.WithinDescription)
        {
            _messageTypeRouting = _messageTypeRouting.AddOrUpdate(messageType, router);
        }

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

    public RoutingExplanation ExplainRoutingFor(Type messageType)
    {
        var explanation = new RoutingExplanation
        {
            MessageType = messageType.FullNameInCode(),
            IsSystemMessageType = messageType.IsSystemMessageType(),
            LocalRoutingConventionDisabled = Options.LocalRoutingConventionDisabled
        };

        // Mirror findRoutes() exactly so the explanation matches real routing behavior: routes
        // accumulate across sources, and a non-additive (terminating) source short-circuits the
        // chain once the accumulated set is non-empty — even if that source itself produced none.
        var accumulated = new List<IMessageRoute>();
        var terminated = false;
        string? terminatingSourceName = null;

        foreach (var source in Options.RouteSources())
        {
            var step = new RouteSourceStep { Source = source.Describe(this) };

            if (terminated)
            {
                step.SkipReason =
                    $"not consulted — terminating source '{terminatingSourceName}' had already produced routes";
                explanation.Steps.Add(step);
                continue;
            }

            var produced = source.FindRoutes(messageType, this).ToList();
            step.Produced = produced.Select(x => x.Describe()).ToList();
            explanation.Steps.Add(step);

            accumulated.AddRange(produced);

            if (accumulated.Count != 0 && !source.IsAdditive)
            {
                terminated = true;
                terminatingSourceName = step.Source.Name;
            }
        }

        // The authoritative final route set (with any de-duplication applied) is whatever
        // RoutingFor produces and caches.
        explanation.FinalRoutes = RoutingFor(messageType).Routes.Select(x => x.Describe()).ToList();

        return explanation;
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