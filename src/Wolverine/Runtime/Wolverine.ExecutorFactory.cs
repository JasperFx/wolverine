using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Logging;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Partitioning;
using Wolverine.Transports.Local;
using Wolverine.Util;

namespace Wolverine.Runtime;

public partial class WolverineRuntime : IExecutorFactory
{
    IExecutor IExecutorFactory.BuildFor(Type messageType)
    {
        // CritterWatch GH-907: system message types (agent commands, acknowledgements, CritterWatch
        // monitoring traffic) invoked through the runtime-level pipeline must not record execution
        // metrics. Decided here, once per message type, never per envelope.
        if (messageType.IsSystemMessageType())
        {
            var handler = Handlers.HandlerFor(messageType);
            if (handler != null)
            {
                return Executor.Build(this, ExecutionPool, Handlers, handler, SystemTraffic);
            }
        }

        // GH-5138. A message type with one handler chain per handler type is invoked chain by chain
        if (Handlers.GroupFor(messageType) is { Chains.Count: > 1 } group)
        {
            return new MultipleChainInvoker(group,
                chain => Executor.Build(this, ExecutionPool, Handlers, Handlers.HandlerForChain(chain), MessageTracking));
        }

        var executor = Executor.Build(this, ExecutionPool, Handlers, messageType);

        return executor;
    }

    IExecutor IExecutorFactory.BuildFor(Type messageType, Endpoint endpoint)
    {
        // GH-5138. A BatchMessagesOf<T>() batch is a member of T's handler group. Asked first, ahead of the partitioned
        // re-router: a batch's own queue is not a stray copy of a partitioned message
        var handler = Handlers.ResolveBatchMember(messageType, endpoint, Options.Transports.GetOrCreate<LocalTransport>(),
            batch => batch.BuildHandler(this));

        if (handler == null && Options.MessagePartitioning.TryFindTopology(messageType, out var topology))
        {
            if (!topology!.Slots.Contains(endpoint))
            {
                handler = new PartitionedMessageReRouter(topology, messageType);
            }
        }

        handler ??= (IMessageHandler?)Handlers.HandlerFor(messageType, endpoint);
        // An element type with a batch and no handler chain of its own goes into the batch wherever it is received
        if (handler == null && Handlers.BatchFor(messageType) is { } batching)
        {
            handler = batching.BuildHandler(this);
        }

        var tracker = trackerFor(messageType, endpoint);

        var executor = handler == null
            ? new NoHandlerExecutor(messageType, this, tracker)
            : Executor.Build(this, ExecutionPool, Handlers, handler, tracker);

        return executor;
    }

    // CritterWatch GH-907: pick each executor's tracker ONCE at construction. System traffic — a system
    // message type (agent commands, acks, CritterWatch monitoring messages), a system-role endpoint (node
    // control queues, internal local queues), or an endpoint with telemetry deliberately switched off —
    // gets the metrics-silent tracker, in EVERY metrics mode. This used to exclude only IAgentCommand and
    // only from the two CritterWatch-publishing modes, so agent commands still hit the OTel meters in the
    // default mode and CritterWatch's own monitoring messages were counted (and re-published) as
    // application volume — the feedback loop behind an idle app reporting hundreds of messages a minute.
    private IMessageTracker trackerFor(Type messageType, Endpoint endpoint)
    {
        if (messageType.IsSystemMessageType() || endpoint.Role == EndpointRole.System || !endpoint.TelemetryEnabled)
        {
            return SystemTraffic;
        }

        if (Options.Metrics.Mode == WolverineMetricsMode.CritterWatch)
        {
            var accumulator = MetricsAccumulator.FindAccumulator(messageType.ToMessageTypeName(), endpoint);
            return new DirectMetricsPublishingMessageTracker(this, accumulator.EntryPoint);
        }

        if (Options.Metrics.Mode == WolverineMetricsMode.Hybrid)
        {
            var accumulator = MetricsAccumulator.FindAccumulator(messageType.ToMessageTypeName(), endpoint);
            return new HybridMetricsPublishingMessageTracker(this, accumulator.EntryPoint);
        }

        return this;
    }
}
