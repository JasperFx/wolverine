using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Routing;
using Wolverine.Transports.Local;

namespace Wolverine.Runtime.Partitioning;

/// <summary>
/// GH-5916. What the handler scoping rules need from a partitioned topology, local, external sharded or global
/// </summary>
internal interface IPartitionedTopology
{
    /// <summary>
    /// How the topology is named in startup errors
    /// </summary>
    string Name { get; }

    /// <summary>
    /// False for a topology that only publishes, and so never runs a handler in this application
    /// </summary>
    bool ListensLocally { get; }

    /// <summary>
    /// The handler types declared with <c>HandledBy()</c>
    /// </summary>
    IReadOnlyList<Type> HandlerTypes { get; }

    bool Matches(Type messageType);

    /// <summary>
    /// Whether messages received at <paramref name="endpoint"/> are executed for this topology: a local or sharded
    /// slot, or a global topology's companion local queue (or its external slot under native acks)
    /// </summary>
    bool ExecutesAt(Endpoint endpoint);

    bool TryMatch(Type messageType, IWolverineRuntime runtime, out IMessageRoute? route);
}

/// <summary>
/// GH-5916. A message type published through more than one partitioned topology that runs handlers in this
/// application has each of its handler types run by the one topology that declares it with <c>HandledBy()</c>.
/// Without that, every topology's slots ran every handler, so each handler ran once per topology. A message type
/// published through a single topology is not affected: every one of its handlers runs on that topology, as before
/// </summary>
internal class TopologyHandlerScopes
{
    private readonly MessagePartitioningRules _partitioning;

    public TopologyHandlerScopes(MessagePartitioningRules partitioning)
    {
        _partitioning = partitioning;
    }

    private IPartitionedTopology[] executingTopologiesFor(Type messageType)
    {
        return _partitioning.AllTopologies.Where(x => x.ListensLocally && x.Matches(messageType)).ToArray();
    }

    private static bool claims(IPartitionedTopology topology, HandlerChain chain)
    {
        return topology.HandlerTypes.Contains(MessageHandlerGroup.HandlerTypeOf(chain));
    }

    /// <summary>
    /// The handler chains of <paramref name="group"/>'s message type that run on <paramref name="topology"/>'s slots:
    /// the ones it declares when the message type is published through more than one executing topology, otherwise
    /// all of them
    /// </summary>
    internal HandlerChain[] ChainsRunBy(IPartitionedTopology topology, MessageHandlerGroup group)
    {
        if (executingTopologiesFor(group.MessageType).Length < 2) return group.Chains.ToArray();

        return group.Chains.Where(x => claims(topology, x)).ToArray();
    }

    /// <summary>
    /// Whether a message of this type is routed through <paramref name="topology"/>. When the message type is handled
    /// here and more than one executing topology publishes it, only the topologies that declare one of its handler
    /// types get a copy. A topology that only publishes is always routed to
    /// </summary>
    internal bool RoutesThrough(IPartitionedTopology topology, Type messageType, HandlerGraph handlers)
    {
        if (!topology.ListensLocally) return true;

        if (handlers.GroupFor(messageType) is not { Chains.Count: > 0 } group) return true;
        if (executingTopologiesFor(messageType).Length < 2) return true;

        return group.Chains.Any(x => claims(topology, x));
    }

    /// <summary>
    /// The handler chains to run for a message received at one of the slots of a topology that declares handler
    /// types for it, or null when the usual handler resolution applies
    /// </summary>
    internal HandlerChain[]? ChainsScopedTo(Type messageType, Endpoint endpoint, HandlerGraph handlers)
    {
        if (handlers.GroupFor(messageType) is not { Chains.Count: > 0 } group) return null;

        var executing = executingTopologiesFor(messageType);
        if (executing.Length < 2) return null;

        var topology = executing.FirstOrDefault(x => x.ExecutesAt(endpoint));
        if (topology == null) return null;

        var chains = group.Chains.Where(x => claims(topology, x)).ToArray();
        return chains.Length == 0 ? null : chains;
    }

    /// <summary>
    /// Builds what runs a message on a topology slot from the chains that topology declares: the one chain right there
    /// on the slot, or a relay to the local queues of several
    /// </summary>
    internal static IMessageHandler BuildHandler(Type messageType, HandlerChain[] chains, HandlerGraph handlers)
    {
        if (chains.Length == 1) return handlers.HandlerForChain(chains[0]);

        var queues = chains.SelectMany(x => x.Endpoints).OfType<LocalQueue>().Select(x => x.Uri).Distinct().ToArray();
        return handlers.BuildFanoutHandler(messageType, chains[0], queues);
    }

    /// <summary>
    /// GH-5695. Every message type of a global partitioned topology that the topology's slots run in more than one
    /// handler type. Each handler type runs on its own local queue, so the slot relays the message to those queues and
    /// moves on, and nothing keeps one group's messages in order in any of them. Logged as a warning at startup rather
    /// than refused, because applications start with this configuration today (GH-2303's sticky handlers)
    /// </summary>
    internal IEnumerable<(GlobalPartitionedMessageTopology Topology, Type MessageType, HandlerChain[] Chains)>
        GlobalTopologiesThatLoseGroupOrdering(HandlerGraph handlers)
    {
        foreach (var group in handlers.Groups)
        {
            if (group.Chains.Count < 2) continue;

            foreach (var topology in _partitioning.GlobalPartitionedTopologies)
            {
                if (!topology.Matches(group.MessageType)) continue;

                // GH-5916. A topology that is one of several publishing the message type runs only the handler types
                // it declares, and a single declared handler type runs right on the slot, in order
                var chains = ChainsRunBy(topology, group);
                if (chains.Length > 1)
                {
                    yield return (topology, group.MessageType, chains);
                }
            }
        }
    }

    /// <summary>
    /// Refuses, at startup, a handler type declared on two topologies, and any message type published through more
    /// than one executing topology whose handler types are not each declared on one of them
    /// </summary>
    internal void AssertValid(HandlerGraph handlers)
    {
        var declarations = _partitioning.AllTopologies
            .SelectMany(topology => topology.HandlerTypes.Distinct().Select(handlerType => (handlerType, topology)))
            .GroupBy(x => x.handlerType)
            .FirstOrDefault(x => x.Count() > 1);

        if (declarations != null)
        {
            throw new InvalidOperationException(
                $"Handler type '{declarations.Key.FullNameInCode()}' is declared with HandledBy() on more than one " +
                $"partitioned topology ({declarations.Select(x => x.topology.Name).Join(", ")}). A handler type can " +
                "run on one partitioned topology only. Declare it on just one of them.");
        }

        foreach (var group in handlers.Groups)
        {
            if (group.Chains.Count == 0) continue;

            var executing = executingTopologiesFor(group.MessageType);
            if (executing.Length < 2) continue;

            var messageType = group.MessageType.FullNameInCode();
            var names = executing.Select(x => x.Name).Join(", ");

            if (executing.All(x => x.HandlerTypes.Count == 0))
            {
                throw new InvalidOperationException(
                    $"Message type '{messageType}' is published through more than one partitioned topology ({names}), " +
                    $"and none of them declares which handler types it runs, so every handler ({group.DescribeHandlerTypes()}) " +
                    "would run once per topology. Declare each handler type on the topology that should run it with " +
                    "HandledBy<THandler>() inside the topology's configuration, or leave the message type out of all but " +
                    "one of the topologies.");
            }

            var unclaimed = group.Chains.Where(chain => !executing.Any(t => claims(t, chain))).ToArray();
            if (unclaimed.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Message type '{messageType}' is published through more than one partitioned topology ({names}), " +
                    $"but no topology declares its handler type(s) {MessageHandlerGroup.Describe(unclaimed)}. Declare each " +
                    "one with HandledBy<THandler>() on the topology that should run it.");
            }
        }
    }
}
