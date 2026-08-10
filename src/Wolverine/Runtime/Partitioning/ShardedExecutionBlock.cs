using System.Diagnostics;
using JasperFx.Blocks;
using Wolverine.Transports;

namespace Wolverine.Runtime.Partitioning;

internal class ShardedExecutionBlock : BlockBase<Envelope>
{
    private readonly int _numberOfSlots;
    private readonly MessagePartitioningRules _rules;
    private readonly Block<Envelope>[] _slots;

    // GH-3899: message types exempted from partitioned processing execute here at the endpoint's
    // normal parallelism instead of being serialized behind a GroupId slot. Null when the
    // application has registered no exemptions, so the default behavior is completely unchanged.
    private readonly Block<Envelope>? _exemptLane;

    public ShardedExecutionBlock(int numberOfSlots, MessagePartitioningRules rules, Func<Envelope, CancellationToken, Task> processAsync)
        : this(numberOfSlots, rules, Block<Envelope>.DefaultBoundedCapacity, processAsync)
    {
    }

    public ShardedExecutionBlock(int numberOfSlots, MessagePartitioningRules rules, int boundedCapacity,
        Func<Envelope, CancellationToken, Task> processAsync, int? exemptLaneParallelism = null)
    {
        _numberOfSlots = numberOfSlots;
        _rules = rules;

        _slots = new Block<Envelope>[_numberOfSlots];
        for (int i = 0; i < _numberOfSlots; i++)
        {
            _slots[i] = new Block<Envelope>(1, boundedCapacity, processAsync);
        }

        if (rules.HasProcessingExemptions)
        {
            _exemptLane = new Block<Envelope>(exemptLaneParallelism ?? numberOfSlots, boundedCapacity, processAsync);
        }
    }

    internal bool HasExemptLane => _exemptLane != null;

    private bool tryRouteToExemptLane(Envelope item, out Block<Envelope> lane)
    {
        // Envelope.Message is guaranteed to be populated on this path for real listeners because
        // DeserializeFirst() runs upstream of the slot routing, but stay defensive: an envelope
        // without a resolved message keeps the (safe) partitioned path.
        if (_exemptLane != null && item.Message != null &&
            _rules.IsExemptFromPartitionedProcessing(item.Message.GetType()))
        {
            lane = _exemptLane;
            return true;
        }

        lane = default!;
        return false;
    }

    public IBlock<Envelope> DeserializeFirst(IHandlerPipeline pipeline, IWolverineRuntime runtime, IChannelCallback channel)
    {
        // Deserialize parallelism beyond the slot count buys nothing (the slots become the
        // bottleneck again), and going wider than the machine can't help CPU-bound decompression
        return DeserializeFirst(pipeline, runtime, channel, Math.Min(_numberOfSlots, Environment.ProcessorCount));
    }

    /// <summary>
    /// Builds the upstream decompress/deserialize stage in front of the slot blocks (GH-3900).
    ///
    /// ORDERING INVARIANT: envelopes must reach the slot enqueue (<see cref="PostAsync"/>, where the
    /// GroupId -> slot hash runs) in their exact arrival order. Each slot is a single-worker block, so
    /// arrival-ordered slot enqueue is what yields the per-GroupId FIFO this whole structure exists for.
    /// A naive N-worker stage (PushUpstream(parallelCount, ...)) would NOT preserve this: parallel
    /// Block workers complete in arbitrary order, so two same-group envelopes could swap before the
    /// slot hash ever sees them. Nor can the stage itself be hash-partitioned by group: the GroupId
    /// header is only present when the sender set it, and receiver-side grouping rules (ByMessage,
    /// ByPropertyNamed, inferred grouping) need the DESERIALIZED message to resolve a group at all.
    ///
    /// So instead: pipelined deserialization with ordered emission. A single-worker ingress stage
    /// starts up to <paramref name="parallelism"/> concurrent deserialization tasks and posts
    /// (envelope, task) pairs downstream in arrival order; a single-worker emit stage awaits each
    /// task in that same order and hands the envelope to the slot hash. The CPU-bound work runs
    /// N-wide, while the slot enqueue order stays byte-for-byte what the old serial stage produced.
    ///
    /// Interaction with the GH-3899 exempt lane: exempted envelopes ride this same pipeline --
    /// exemption is keyed on the message TYPE, which (like the group) is only knowable after
    /// deserialization, so there is no earlier point to divert them. They are deserialized exactly
    /// once, and the exempt-lane routing happens inside <see cref="PostAsync"/>, downstream of the
    /// ordered emission; posting to the multi-worker exempt lane never blocks emission any harder
    /// than posting to a slot does (same bounded-channel back pressure).
    /// </summary>
    public IBlock<Envelope> DeserializeFirst(IHandlerPipeline pipeline, IWolverineRuntime runtime, IChannelCallback channel, int parallelism)
    {
        async Task<Envelope?> deserializeAsync(Envelope e)
        {
            var continuation = await pipeline.TryDeserializeEnvelope(e);
            if (continuation is NullContinuation)
            {
                return e;
            }

            var envelopeLifecycle = new MessageContext(runtime);
            envelopeLifecycle.ReadEnvelope(e, channel);
            await continuation.ExecuteAsync(envelopeLifecycle, runtime, DateTimeOffset.UtcNow, Activity.Current);

            return null;
        }

        if (parallelism <= 1)
        {
            // The original serial stage: one worker deserializes and posts inline
            return PushUpstream<Envelope>(async (e, _) => (await deserializeAsync(e))!);
        }

        // Caps the number of concurrent deserialization tasks. Released by each task itself on
        // completion (success or failure), so the cap tracks actual in-flight CPU work. Never
        // disposed on purpose: SemaphoreSlim only needs disposal when AvailableWaitHandle is used,
        // and the ingress block owning the only waiter is torn down by the BlockSet
        var gate = new SemaphoreSlim(parallelism, parallelism);

        // Emit stage: single worker awaiting the pipelined tasks strictly in arrival order.
        // Awaiting in order IS the re-sequencing -- out-of-order completions simply wait their turn
        var emit = PushUpstream<DeserializationWork>(async (work, _) => (await work.Work)!);

        // A deserialization failure surfaces here on the await. Route it to this block's OnError
        // (which the receivers point at their logging) with the actual envelope -- the emit block's
        // own default sink would only see the opaque work item. Read OnError at invocation time so
        // the receiver's later assignment is honored. `work` is null when the emit block faults
        // terminally (jasperfx#506): forward the null unchanged, because that null item is exactly
        // how the receivers recognize a terminal block fault (HasFaulted / CritterWatch#942)
        emit.OnError = (work, ex) => OnError(work?.Envelope!, ex);

        return emit.PushUpstream<Envelope>(async (envelope, cancellation) =>
        {
            try
            {
                await gate.WaitAsync(cancellation);
            }
            catch (OperationCanceledException)
            {
                // Block shutdown; returning null skips the downstream post
                return null!;
            }

            // Deliberately no cancellation token on Task.Run: the finally MUST run so the gate
            // is always released, and the emit stage must always have a completed/faulted task
            // to await rather than one that was never started
            var work = Task.Run(async () =>
            {
                try
                {
                    return await deserializeAsync(envelope);
                }
                finally
                {
                    gate.Release();
                }
            });

            return new DeserializationWork(envelope, work);
        });
    }

    /// <summary>
    /// An arrival-ordered handle to an in-flight deserialization. Deliberately a reference type:
    /// PushUpstream skips posting null transformations, which is how a cancelled ingress item
    /// avoids sending a default struct (with a null Task) into the emit stage
    /// </summary>
    private sealed record DeserializationWork(Envelope Envelope, Task<Envelope?> Work);

    private IEnumerable<Block<Envelope>> allBlocks()
    {
        foreach (var slot in _slots)
        {
            yield return slot;
        }

        if (_exemptLane != null)
        {
            yield return _exemptLane;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var slot in allBlocks())
        {
            try
            {
                await slot.DisposeAsync();
            }
            catch (Exception)
            {
                // Not allowing any exception to escape here
            }
        }
    }

    public override async Task WaitForCompletionAsync()
    {
        foreach (var slot in allBlocks())
        {
            await slot.WaitForCompletionAsync();
        }
    }

    public override void Complete()
    {
        foreach (var slot in allBlocks())
        {
            slot.Complete();
        }
    }

    public override uint Count => (uint)allBlocks().Sum(x => x.Count);

    /// <summary>
    /// Propagates to every slot block. Without this, a slot's escaping exception falls to the
    /// JasperFx Block default sink (stderr) — invisible to anyone reading structured logs.
    /// </summary>
    public override Action<Envelope, Exception> OnError
    {
        get => _slots[0].OnError;
        set
        {
            foreach (var slot in allBlocks())
            {
                slot.OnError = value;
            }
        }
    }

    public override ValueTask PostAsync(Envelope item)
    {
        // GH-3899: message types that are explicitly exempted from partitioned processing skip
        // the GroupId slots entirely and execute at the endpoint's normal parallelism, so one
        // dominant GroupId cannot serialize message types that need no ordering
        if (tryRouteToExemptLane(item, out var lane))
        {
            return lane.PostAsync(item);
        }

        // This first uses new "message grouping rules" to determine a GroupId
        // for an envelope if there's not already one, then...
        // Does a deterministic hash of the GroupId, then a modulo of the number
        // of slots to get the slot number it should use...
        // then publishes that message to a single file channel for processing
        // This way any message w/ the same GroupId is always handled in the
        // same channel slot.
        // So, parallelism between message groups, but sequential within the group
        var index = item.SlotForProcessing(_numberOfSlots, _rules);
        return _slots[index].PostAsync(item);
    }

    public override void Post(Envelope item)
    {
        if (tryRouteToExemptLane(item, out var lane))
        {
            lane.Post(item);
            return;
        }

        var index = item.SlotForProcessing(_numberOfSlots, _rules);
        _slots[index].Post(item);
    }
}