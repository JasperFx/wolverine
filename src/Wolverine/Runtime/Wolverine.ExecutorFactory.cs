using JasperFx.Core.Reflection;
using Wolverine.Configuration;
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
        var executor = Executor.Build(this, ExecutionPool, Handlers, messageType);

        return executor;
    }

    IExecutor IExecutorFactory.BuildFor(Type messageType, Endpoint endpoint)
    {
        IMessageHandler? handler = null;

        // If this endpoint is the dedicated batch queue for the element type, always use the
        // batching processor — even when the element type also has a direct Handle(T) handler
        // (the Separated direct-handler + batch case, where HandlerFor would otherwise return
        // the direct handler and shadow the batch).
        var batchForEndpoint = Options.BatchDefinitions.FirstOrDefault(x =>
            x.ElementType == messageType &&
            string.Equals(endpoint.EndpointName, x.LocalExecutionQueueName, StringComparison.OrdinalIgnoreCase));
        if (batchForEndpoint != null)
        {
            handler = batchForEndpoint.BuildHandler(this);
        }

        // External-arrival parity (Separated): when an element type that has BOTH a direct
        // handler and a batch definition arrives on a non-local endpoint, relay it to both
        // local queues (direct + batch) so both run, mirroring the local fan-out routing.
        if (handler == null && endpoint is not LocalQueue &&
            Options.MultipleHandlerBehavior == MultipleHandlerBehavior.Separated)
        {
            var batchDefinition = Options.BatchDefinitions.FirstOrDefault(x => x.ElementType == messageType);
            var directChain = Handlers.ChainFor(messageType);
            if (batchDefinition?.LocalExecutionQueueName is { } batchQueueName && directChain != null)
            {
                var local = Options.Transports.GetOrCreate<LocalTransport>();
                var directQueueUri = local.FindQueueForMessageType(messageType).Uri;
                var batchQueueUri = local.QueueFor(batchQueueName).Uri;
                handler = Handlers.BuildFanoutHandler(messageType, directChain, [directQueueUri, batchQueueUri]);
            }
        }

        if (handler == null && Options.MessagePartitioning.TryFindTopology(messageType, out var topology))
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

        IMessageTracker tracker = this;
        if (!messageType.CanBeCastTo<IAgentCommand>() && Options.Metrics.Mode == WolverineMetricsMode.CritterWatch)
        {
            var accumulator = MetricsAccumulator.FindAccumulator(messageType.ToMessageTypeName(), endpoint);
            tracker = new DirectMetricsPublishingMessageTracker(this, accumulator.EntryPoint);
        }
        else if (!messageType.CanBeCastTo<IAgentCommand>() && Options.Metrics.Mode == WolverineMetricsMode.Hybrid)
        {
            var accumulator = MetricsAccumulator.FindAccumulator(messageType.ToMessageTypeName(), endpoint);
            tracker = new HybridMetricsPublishingMessageTracker(this, accumulator.EntryPoint);
        }
        
        var executor = handler == null
            ? new NoHandlerExecutor(messageType, this)
            : Executor.Build(this, ExecutionPool, Handlers, handler, tracker);

        return executor;
    }
}