using JasperFx.Core;
using Wolverine.Runtime.Batching;
using Wolverine.Transports.Local;

namespace Wolverine;

public sealed partial class WolverineOptions
{
    internal List<BatchingOptions> BatchDefinitions { get; } = new();

    internal bool AssertsNoBatchHandlerConflicts { get; private set; }

    /// <summary>
    /// Make Wolverine throw at startup (instead of only logging a warning) if a message type has both a
    /// direct <c>Handle(T)</c> handler and a <c>BatchMessagesOf&lt;T&gt;()</c> batch handler under the
    /// default <see cref="MultipleHandlerBehavior.ClassicCombineIntoOneLogicalHandler"/> mode — a
    /// configuration in which the direct handler wins and the batch handler is silently shadowed. Has
    /// no effect under <see cref="MultipleHandlerBehavior.Separated"/>, where both handlers legitimately
    /// run and Wolverine moves the batch onto its own queue.
    /// </summary>
    public void AssertNoBatchHandlerConflicts()
    {
        AssertsNoBatchHandlerConflicts = true;
    }

    /// <summary>
    /// Configure batch processing of an incoming (or local) message type. Note that Wolverine
    /// will require you to use the Array of that element type for the actual batch handler
    /// </summary>
    /// <param name="configure">Optional fine-tuning of the batch size, triggering, and local queue</param>
    /// <typeparam name="T">The message type to be batched</typeparam>
    /// <returns></returns>
    public LocalQueueConfiguration BatchMessagesOf<T>(Action<BatchingOptions>? configure = null)
    {
        return BatchMessagesOf(typeof(T), configure);
    }
    
    /// <summary>
    /// Configure batch processing of an incoming (or local) message type. Note that Wolverine
    /// will require you to use the Array of that element type for the actual batch handler
    /// </summary>
    /// <param name="elementType">The message type to be batched</param>
    /// <param name="configure">Optional fine-tuning of the batch size, triggering, and local queue</param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public LocalQueueConfiguration BatchMessagesOf(Type elementType, Action<BatchingOptions>? configure = null)
    {
        if (elementType == null)
        {
            throw new ArgumentNullException(nameof(elementType));
        }
        
        // GH-1076
        HandlerGraph.RegisterMessageType(elementType);

        var options = new BatchingOptions(elementType);
        var localQueue = Transports.GetOrCreate<LocalTransport>().FindQueueForMessageType(elementType);
        options.LocalExecutionQueueName = localQueue.EndpointName;

        configure?.Invoke(options);

        if (options.LocalExecutionQueueName.IsEmpty())
            throw new InvalidOperationException("A local queue name is required");
        
        BatchDefinitions.Add(options);

        var localQueueConfiguration = LocalQueue(options.LocalExecutionQueueName);
        
        return localQueueConfiguration;
    }
}