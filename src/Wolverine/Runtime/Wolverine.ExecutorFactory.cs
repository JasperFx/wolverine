using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Logging;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Partitioning;
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

        var executor = Executor.Build(this, ExecutionPool, Handlers, messageType);

        return executor;
    }

    IExecutor IExecutorFactory.BuildFor(Type messageType, Endpoint endpoint)
    {
        IMessageHandler? handler = null;
        if (Options.MessagePartitioning.TryFindTopology(messageType, out var topology))
        {
            if (!topology!.Slots.Contains(endpoint))
            {
                handler = new PartitionedMessageReRouter(topology, messageType);
            }
        }

        handler ??= (IMessageHandler?)Handlers.HandlerFor(messageType, endpoint);
        if (handler == null )
        {
            var batching = Options.BatchDefinitions.FirstOrDefault(x => x.ElementType == messageType);
            if (batching != null)
            {
                handler = batching.BuildHandler(this);
            }
        }

        var tracker = trackerFor(messageType, endpoint);

        var executor = handler == null
            ? new NoHandlerExecutor(messageType, this)
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
