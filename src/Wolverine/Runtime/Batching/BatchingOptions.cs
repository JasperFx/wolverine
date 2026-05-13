using System.Diagnostics.CodeAnalysis;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.WorkerQueues;

namespace Wolverine.Runtime.Batching;

public class BatchingOptions : IAsyncDisposable
{
    private IMessageHandler _handler = null!;

    // CloseAndBuildAs over DefaultMessageBatcher<elementType> and
    // ProcessorBuilder<ElementType> closes a generic over the user-supplied
    // batch element message type. Same reflective shape as chunks D/I/J/K.
    // AOT-clean apps in TypeLoadMode.Static keep the element types statically
    // rooted via handler registration and pre-built batchers; opts.BatchMessagesOf
    // call sites have the closed-generic instantiations baked into the
    // source-generated code. Apps that need batching on Native AOT supply their
    // own IMessageBatcher (see BatchingOptions.Batcher setter) — see AOT guide.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "DefaultMessageBatcher/ProcessorBuilder closed over runtime element type; AOT consumers register batchers explicitly. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "DefaultMessageBatcher/ProcessorBuilder closed over runtime element type; AOT consumers register batchers explicitly. See AOT guide.")]
    public BatchingOptions(Type elementType)
    {
        ElementType = elementType;
        Batcher = typeof(DefaultMessageBatcher<>).CloseAndBuildAs<IMessageBatcher>(elementType);
    }

    /// <summary>
    /// The message type to be batched up
    /// </summary>
    public Type ElementType { get; }

    public IMessageBatcher Batcher { get; set; }

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

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "ProcessorBuilder<> closed over runtime ElementType; AOT consumers register batchers explicitly. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "ProcessorBuilder<> closed over runtime ElementType; AOT consumers register batchers explicitly. See AOT guide.")]
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

            var localQueue = (ILocalQueue)runtime.Endpoints.AgentForLocalQueue(options.LocalExecutionQueueName!);

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