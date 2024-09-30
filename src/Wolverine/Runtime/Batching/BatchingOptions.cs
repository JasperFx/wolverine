using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.WorkerQueues;

namespace Wolverine.Runtime.Batching;

public class BatchingOptions(Type elementType) : IAsyncDisposable
{
    private IMessageHandler _handler;
    
    /// <summary>
    /// The message type to be batched up
    /// </summary>
    public Type ElementType { get; } = elementType;

    public IMessageBatcher Batcher { get; set; } =
        typeof(DefaultMessageBatcher<>).CloseAndBuildAs<IMessageBatcher>(elementType);

    /// <summary>
    /// The maximum size of the message batch. Default is 100.
    /// </summary>
    public int BatchSize { get; set; } = 100;
    
    /// <summary>
    /// The time at which a batch will be triggered and processed if there any messages waiting,
    /// but fewer than the batch size. Default is 250ms. You may want to slow this down
    /// for larger batches
    /// </summary>
    public TimeSpan TriggerTime { get; set; } = 250.Milliseconds();
    
    /// <summary>
    /// The name of the local queue where the batch messages will be executed. Note
    /// that the default is the local queue for the batch element message type
    /// </summary>
    public string? LocalExecutionQueueName { get; set; }

    internal IMessageHandler BuildHandler(WolverineRuntime runtime)
    {
        if (_handler != null) return _handler;
        
        var builder = typeof(ProcessorBuilder<>).CloseAndBuildAs<IProcessorBuilder>(ElementType);
        _handler = builder.Build(runtime, Batcher, this);

        return _handler;
    }

    private interface IProcessorBuilder
    {
        IMessageHandler Build(WolverineRuntime runtime, IMessageBatcher batcher, BatchingOptions batchingOptions);
    }

    private class ProcessorBuilder<T> : IProcessorBuilder
    {
        public IMessageHandler Build(WolverineRuntime runtime, IMessageBatcher batcher, BatchingOptions options)
        {
            var parentChain = runtime.Handlers.ChainFor(batcher.BatchMessageType);
            if (parentChain == null)
            {
                throw new InvalidOperationException(
                    $"This Wolverine application has a configuration for batching messages of type {typeof(T).FullNameInCode()}, but there is no known handler for {typeof(T).FullNameInCode()}[]");
            }

            var localQueue = (ILocalQueue)runtime.Endpoints.AgentForLocalQueue(options.LocalExecutionQueueName);

            return new BatchingProcessor<T>(parentChain, batcher, options, localQueue,
                runtime.DurabilitySettings);
        }
    }

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (_handler is IAsyncDisposable ad) return ad.DisposeAsync();

        return new ValueTask();
    }
}