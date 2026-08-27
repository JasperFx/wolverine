using System.Diagnostics.CodeAnalysis;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.WorkerQueues;

namespace Wolverine.Runtime.Batching;

public class BatchingOptions : IAsyncDisposable
{
    private volatile IMessageHandler _handler = null!;

    // GH-4167. BuildHandler runs on the message-handling path, not at bootstrap, so it has to be
    // safe for concurrent first-touch. See the note there for why a duplicate is not benign.
    private readonly object _handlerLock = new();

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
    /// De-duplicate the batched messages by a key so the handler only ever sees one message per
    /// distinct key (the last message for that key wins). This is sugar over the <see cref="Batcher"/>
    /// seam: it installs a <c>CoalescingMessageBatcher&lt;T,TKey&gt;</c>. It only changes what the
    /// handler sees - every member envelope still settles with the batch, exactly like a normal batch.
    /// The lambda parameter must be explicitly typed to the batched element type so both type
    /// arguments can be inferred, e.g. <c>CoalesceBy((RecalculateScores x) =&gt; x.AggregateId)</c>.
    /// </summary>
    /// <param name="keySelector">Selects the de-duplication key from each message</param>
    /// <typeparam name="T">The batched element type (must match <see cref="ElementType"/>)</typeparam>
    /// <typeparam name="TKey">The de-duplication key type</typeparam>
    public void CoalesceBy<T, TKey>(Func<T, TKey> keySelector)
    {
        if (keySelector == null)
        {
            throw new ArgumentNullException(nameof(keySelector));
        }

        if (typeof(T) != ElementType)
        {
            throw new ArgumentOutOfRangeException(nameof(keySelector),
                $"The coalescing key selector must accept the batched element type '{ElementType.FullNameInCode()}', but accepted '{typeof(T).FullNameInCode()}'. Use an explicitly typed lambda parameter, e.g. CoalesceBy(({ElementType.Name} x) => x.SomeKey).");
        }

        Batcher = new CoalescingMessageBatcher<T, TKey>(keySelector);
    }

    /// <summary>
    /// Batch the messages by their <see cref="Envelope.GroupId"/> (and tenant) rather than by tenant
    /// alone, and stamp that group id onto each batch envelope. Use this when the batched handler has
    /// to participate in the same partitioned sequential ordering as the unbatched handlers for the
    /// same entity — for example when several message types all write to one event stream and the
    /// batched one must not race the others.
    /// </summary>
    /// <remarks>
    /// Without this, a batch envelope carries no group id, and an envelope with no group id is
    /// assigned a <b>random</b> partition slot rather than being left unpartitioned — so a batched
    /// handler silently opts out of the sequential guarantee its unbatched siblings have.
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "GroupIdMessageBatcher<> closed over runtime ElementType; AOT consumers register batchers explicitly. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "GroupIdMessageBatcher<> closed over runtime ElementType; AOT consumers register batchers explicitly. See AOT guide.")]
    public void GroupByGroupId()
    {
        Batcher = typeof(GroupIdMessageBatcher<>).CloseAndBuildAs<IMessageBatcher>(ElementType);
    }

    internal int? ProbeIndividuallyAfterAttempts { get; private set; }

    /// <summary>
    /// After the whole batch has failed <paramref name="attempts"/> times, re-run each member as its own
    /// size-1 batch so that only the message actually causing the failure is dead-lettered while the
    /// healthy members succeed. Use this for <em>opaque</em> failures where the handler cannot name the bad
    /// item. Bounded and one-time: a member reduced to a size-1 batch is dead-lettered rather than probed
    /// again. For failures the handler can attribute to a specific exception type, prefer the
    /// <c>OnException&lt;T&gt;().IsolateBatchMembers()</c> policy instead. See GH-3289.
    /// </summary>
    /// <param name="attempts">The number of whole-batch failures to allow before probing. Must be >= 1.</param>
    public void ProbeIndividuallyAfter(int attempts)
    {
        if (attempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attempts), "The probe threshold must be at least 1");
        }

        ProbeIndividuallyAfterAttempts = attempts;
    }

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
    /// <remarks>
    /// Setting this explicitly also opts the batch out of the partitioned execution described on
    /// <see cref="ExecuteOnDedicatedLocalQueue" /> — naming a queue is taken as meaning it.
    /// </remarks>
    public string? LocalExecutionQueueName
    {
        get => _localExecutionQueueName;
        set
        {
            _localExecutionQueueName = value;
            IsLocalQueueExplicit = true;
        }
    }

    private string? _localExecutionQueueName;

    /// <summary>
    /// Wolverine's own default assignment of the execution queue, which must not read as a user
    /// choice. Used by <c>WolverineOptions.BatchMessagesOf</c> and by the Separated-mode queue
    /// reassignment at startup.
    /// </summary>
    internal void SetDefaultLocalExecutionQueueName(string queueName)
    {
        _localExecutionQueueName = queueName;
    }

    /// <summary>True when the application named the execution queue rather than Wolverine defaulting it.</summary>
    internal bool IsLocalQueueExplicit { get; private set; }

    internal bool ForcesDedicatedQueue { get; private set; }

    /// <summary>
    /// Always run the assembled batches on this batch's own dedicated local queue, even when the
    /// element type belongs to a partitioned message topology. By default a batched element type
    /// that participates in <c>GlobalPartitioned</c> or <c>PublishToPartitionedLocalMessaging</c>
    /// executes its batches on the topology slot for the batch's group id, so that the batched
    /// handler is sequenced against the unbatched handlers for that same group. Call this to opt
    /// out and accept that the batch runs concurrently with them. See GH-3867.
    /// </summary>
    public void ExecuteOnDedicatedLocalQueue()
    {
        ForcesDedicatedQueue = true;
    }

    /// <summary>
    /// The partitioned topology slots that assembled batches are distributed across by group id,
    /// resolved at startup. Null when this batch executes on a single dedicated local queue.
    /// </summary>
    internal IReadOnlyList<Endpoint>? ExecutionSlots { get; set; }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "ProcessorBuilder<> closed over runtime ElementType; AOT consumers register batchers explicitly. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "ProcessorBuilder<> closed over runtime ElementType; AOT consumers register batchers explicitly. See AOT guide.")]
    internal IMessageHandler BuildHandler(WolverineRuntime runtime)
    {
        if (_handler != null) return _handler;

        // GH-4167. This is lazy init on the message-handling path, and the caller reaches it through
        // HandlerPipeline's LightweightCache<Type, IExecutor>, whose indexer does NOT lock: two
        // concurrent misses each invoke the factory AND each returns its own instance. A duplicate
        // stateless executor is harmless, which is why that cache is fine everywhere else. A duplicate
        // BatchingProcessor is not -- each one owns a separate BatchingChannel buffer, its own flush
        // Timer, and two Blocks with live worker tasks. Two instances means the batch silently splits
        // across them, and the loser is never handed back to anyone, so it is never disposed: its timer
        // and worker tasks leak for the life of the process.
        //
        // Reproduced deterministically once JasperFx stopped running Block continuations inline on the
        // publisher (jasperfx#714) -- that inline execution had been serializing the first messages onto
        // one thread and hiding this. The race was always reachable under genuinely concurrent traffic.
        lock (_handlerLock)
        {
            if (_handler != null) return _handler;

            var builder = typeof(ProcessorBuilder<>).CloseAndBuildAs<IProcessorBuilder>(ElementType);
            _handler = builder.Build(runtime, Batcher, this);
        }

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

            // A batcher that groups by group id needs the application's partitioning rules to
            // resolve a group id for any envelope that does not already carry one. The runtime is
            // not available when BatchingOptions is configured, so it is supplied here.
            if (batcher is IRequirePartitioningRules needsRules)
            {
                needsRules.Rules = runtime.Options.MessagePartitioning;
            }

            var localQueue = (ILocalQueue)runtime.Endpoints.AgentForLocalQueue(options.LocalExecutionQueueName!);

            var queues = buildExecutionQueues(runtime, options, localQueue);

            return new BatchingProcessor<T>(parentChain, batcher, options, localQueue, queues,
                runtime.DurabilitySettings, runtime.BatchingPendingCounts, runtime.Logger);
        }

        /// <summary>
        /// GH-3867. When startup found a partitioned topology for the element type, assembled batches
        /// are distributed across that topology's local queues by group id instead of all landing on
        /// the one dedicated queue.
        /// </summary>
        private static IBatchExecutionQueues buildExecutionQueues(WolverineRuntime runtime,
            BatchingOptions options, ILocalQueue dedicatedQueue)
        {
            if (options.ExecutionSlots is not { Count: > 0 } slots)
            {
                return new SingleBatchExecutionQueue(dedicatedQueue);
            }

            var slotQueues = new ILocalQueue[slots.Count];
            for (var i = 0; i < slots.Count; i++)
            {
                if (runtime.Endpoints.AgentForLocalQueue(slots[i].Uri) is not ILocalQueue queue)
                {
                    throw new InvalidOperationException(
                        $"Unable to resolve local queue '{slots[i].Uri}' as an execution slot for batches of {options.ElementType.FullNameInCode()}");
                }

                slotQueues[i] = queue;
            }

            return new PartitionedBatchExecutionQueues(slotQueues, runtime.Options.MessagePartitioning,
                dedicatedQueue);
        }
    }

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (_handler is IAsyncDisposable ad) return ad.DisposeAsync();

        return new ValueTask();
    }
}