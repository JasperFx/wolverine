using System.Diagnostics;
using JasperFx.Blocks;
using Wolverine.Transports;

namespace Wolverine.Runtime.Sharding;

internal class ShardedExecutionBlock : BlockBase<Envelope>
{
    private readonly int _numberOfSlots;
    private readonly MessagePartitioningRules _rules;
    private readonly Block<Envelope>[] _slots;

    public ShardedExecutionBlock(int numberOfSlots, MessagePartitioningRules rules, Func<Envelope, CancellationToken, Task> processAsync)
    {
        _numberOfSlots = numberOfSlots;
        _rules = rules;

        _slots = new Block<Envelope>[_numberOfSlots];
        for (int i = 0; i < _numberOfSlots; i++)
        {
            _slots[i] = new Block<Envelope>(processAsync);
        }
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

            return default;
        });
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var slot in _slots)
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
        foreach (var slot in _slots)
        {
            await slot.WaitForCompletionAsync();
        }
    }

    public override void Complete()
    {
        foreach (var slot in _slots)
        {
            slot.Complete();
        }
    }

    public override uint Count => (uint)_slots.Sum(x => x.Count);

    public override ValueTask PostAsync(Envelope item)
    {
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
        var index = item.SlotForProcessing(_numberOfSlots, _rules);
        _slots[index].Post(item);
    }
}