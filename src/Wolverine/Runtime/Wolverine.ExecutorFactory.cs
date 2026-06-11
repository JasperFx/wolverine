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
        // Resolve any Separated-mode batch handler that the default HandlerFor lookup would otherwise
        // miss or shadow, in priority order. See each helper for the specific case it covers.
        var handler =
            tryBuildDedicatedBatchQueueHandler(messageType, endpoint)
            ?? tryBuildExternalArrivalFanoutHandler(messageType, endpoint)
            ?? tryBuildMultipleBatchHandlerFanout(messageType, endpoint);

        if (handler == null && Options.MessagePartitioning.TryFindTopology(messageType, out var topology))
        {
            if (!topology!.Slots.Contains(endpoint))
            {
                handler = new PartitionedMessageReRouter(topology, messageType);
            }
        }

        handler ??= (IMessageHandler?)Handlers.HandlerFor(messageType, endpoint);
        if (handler == null)
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

    /// <summary>
    /// If this endpoint is the dedicated batch queue for the element type, always use the batching
    /// processor — even when the element type also has a direct Handle(T) handler (the Separated
    /// direct-handler + batch case, where HandlerFor would otherwise return the direct handler and
    /// shadow the batch).
    /// </summary>
    private IMessageHandler? tryBuildDedicatedBatchQueueHandler(Type messageType, Endpoint endpoint)
    {
        var batchForEndpoint = Options.BatchDefinitions.FirstOrDefault(x =>
            x.ElementType == messageType &&
            string.Equals(endpoint.EndpointName, x.LocalExecutionQueueName, StringComparison.OrdinalIgnoreCase));

        return batchForEndpoint?.BuildHandler(this);
    }

    /// <summary>
    /// External-arrival parity (Separated): when an element type that has BOTH a direct handler and a
    /// batch definition arrives on a non-local endpoint, relay it to both local queues (direct +
    /// batch) so both run, mirroring the local fan-out routing.
    /// </summary>
    private IMessageHandler? tryBuildExternalArrivalFanoutHandler(Type messageType, Endpoint endpoint)
    {
        if (endpoint is LocalQueue || Options.MultipleHandlerBehavior != MultipleHandlerBehavior.Separated)
        {
            return null;
        }

        var batchDefinition = Options.BatchDefinitions.FirstOrDefault(x => x.ElementType == messageType);
        var directChain = Handlers.ChainFor(messageType);
        if (batchDefinition?.LocalExecutionQueueName is { } batchQueueName && directChain != null)
        {
            var local = Options.Transports.GetOrCreate<LocalTransport>();
            var directQueueUri = local.FindQueueForMessageType(messageType).Uri;
            var batchQueueUri = local.QueueFor(batchQueueName).Uri;
            return Handlers.BuildFanoutHandler(messageType, directChain, [directQueueUri, batchQueueUri]);
        }

        return null;
    }

    /// <summary>
    /// Multiple Separated batch handlers: when the produced batch-message type (T[] or a custom batch
    /// type) has MORE THAN ONE Handle handler, Separated mode splits them onto per-handler sticky
    /// queues. The BatchingProcessor re-enqueues a single produced batch onto the batch's own
    /// execution queue — which is none of those sticky queues — so HandlerFor would throw. Relay the
    /// produced batch from that queue to every sticky handler queue via a fan-out.
    /// </summary>
    private IMessageHandler? tryBuildMultipleBatchHandlerFanout(Type messageType, Endpoint endpoint)
    {
        if (endpoint is not LocalQueue || Options.MultipleHandlerBehavior != MultipleHandlerBehavior.Separated)
        {
            return null;
        }

        var thisQueueProducesTheBatch = Options.BatchDefinitions.Any(x =>
            x.Batcher.BatchMessageType == messageType &&
            string.Equals(endpoint.EndpointName, x.LocalExecutionQueueName, StringComparison.OrdinalIgnoreCase));
        if (!thisQueueProducesTheBatch)
        {
            return null;
        }

        var batchChain = Handlers.ChainFor(messageType);
        if (batchChain == null || !batchChain.ByEndpoint.Any() || batchChain.HasDefaultNonStickyHandlers())
        {
            return null;
        }

        var stickyLocalUris = batchChain.ByEndpoint
            .SelectMany(c => c.Endpoints)
            .OfType<LocalQueue>()
            .Select(e => e.Uri)
            .Distinct()
            .ToArray();

        return stickyLocalUris.Length != 0
            ? Handlers.BuildFanoutHandler(messageType, batchChain, stickyLocalUris)
            : null;
    }
}