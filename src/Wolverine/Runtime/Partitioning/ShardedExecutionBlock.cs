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
        return PushUpstream<Envelope>(async (e, _) =>
        {
            var continuation = await pipeline.TryDeserializeEnvelope(e);
            if (continuation is NullContinuation)
            {
                return e;
            }

            var envelopeLifecycle = new MessageContext(runtime);
            envelopeLifecycle.ReadEnvelope(e, channel);
            await continuation.ExecuteAsync(envelopeLifecycle, runtime, DateTimeOffset.UtcNow, Activity.Current);

            return default!;
        });
    }

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