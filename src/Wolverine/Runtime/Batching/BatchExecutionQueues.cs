using JasperFx.Core;
using Wolverine.Runtime.Partitioning;
using Wolverine.Runtime.WorkerQueues;

namespace Wolverine.Runtime.Batching;

/// <summary>
/// Chooses the local queue that an assembled batch executes on. GH-3867.
/// </summary>
internal interface IBatchExecutionQueues
{
    ILocalQueue SelectQueue(Envelope grouped);
}

/// <summary>
/// Every batch runs on one dedicated local queue — the original behavior, and still what you get
/// when the batched element type is not part of a partitioned topology.
/// </summary>
internal class SingleBatchExecutionQueue : IBatchExecutionQueues
{
    private readonly ILocalQueue _queue;

    public SingleBatchExecutionQueue(ILocalQueue queue)
    {
        _queue = queue;
    }

    public ILocalQueue SelectQueue(Envelope grouped) => _queue;
}

/// <summary>
/// Distributes assembled batches across the local queues of the partitioned topology that the
/// batched element type belongs to, choosing the slot from the batch's own group id. This is what
/// puts a batched handler on the same execution block as the unbatched handlers for that group,
/// rather than on a queue of its own where it races them. GH-3867.
/// </summary>
internal class PartitionedBatchExecutionQueues : IBatchExecutionQueues
{
    private readonly ILocalQueue[] _slots;
    private readonly MessagePartitioningRules _rules;
    private readonly ILocalQueue _fallback;

    /// <param name="fallback">
    /// Where a batch with no determinable group id goes. It cannot be slotted — and an envelope with
    /// no group id draws a <em>random</em> slot rather than being left unpartitioned — so it stays on
    /// the dedicated queue instead of scattering. Only reachable with a custom
    /// <see cref="IMessageBatcher" />, since Wolverine swaps the built-in batcher for the group-id
    /// one whenever this type is in play.
    /// </param>
    public PartitionedBatchExecutionQueues(ILocalQueue[] slots, MessagePartitioningRules rules, ILocalQueue fallback)
    {
        if (slots.Length == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slots), "At least one execution slot is required");
        }

        _slots = slots;
        _rules = rules;
        _fallback = fallback;
    }

    public ILocalQueue SelectQueue(Envelope grouped)
    {
        if (grouped.GroupId.IsEmpty())
        {
            return _fallback;
        }

        // SlotForSending, not SlotForProcessing: this is the same layer — and must be the same hash —
        // that GlobalPartitionedRoute and PartitionedMessageTopology.SelectSlot use to place the
        // unbatched messages for this group id.
        return _slots[grouped.SlotForSending(_slots.Length, _rules)];
    }
}
