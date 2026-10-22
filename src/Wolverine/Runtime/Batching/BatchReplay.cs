using System.Diagnostics.CodeAnalysis;
using Wolverine.Runtime.WorkerQueues;

namespace Wolverine.Runtime.Batching;

/// <summary>
/// Shared re-execution primitive for the batch item-isolation features (GH-3289): re-enqueue a reduced
/// batch (a subset of the original members) onto the batch's own local queue so the batch handler runs
/// again over only those items. No separate single-element handler is needed.
/// </summary>
/// <remarks>
/// F1. A reduced batch carries the ORIGINAL member envelopes, not new ones built around their messages. A member
/// keeps its own identity -- its id, its Destination, its inbox row, the listener it arrived on -- so it is settled
/// at the end of its own replay, and a member that fails again is dead lettered as itself. The caller takes the
/// members out of the original batch first (<see cref="Detach"/>), so the original batch's terminal neither marks
/// them handled while their replay exists only in memory nor releases their pending counts: those move with them
/// to the reduced batch, and are released once, at its terminal.
/// </remarks>
internal static class BatchReplay
{
    /// <summary>
    /// Take these members out of a batch, so the batch's own terminal neither settles their inbox rows nor releases
    /// their pending counts. Their fate is now decided elsewhere: a replay, or a hand-back after a failed dead
    /// letter write.
    /// </summary>
    public static void Detach(Envelope batchEnvelope, IReadOnlyCollection<Envelope> members)
    {
        if (batchEnvelope.Batch == null || members.Count == 0) return;

        var remaining = new List<Envelope>(batchEnvelope.Batch.Length);
        foreach (var member in batchEnvelope.Batch)
        {
            if (!containsByReference(members, member))
            {
                remaining.Add(member);
            }
        }

        // Never null: a batch envelope with no Batch is completed as a single envelope with an inbox row of its own,
        // which a batch envelope does not have
        batchEnvelope.Batch = remaining.ToArray();
    }

    /// <summary>
    /// F2. These members could not be dead lettered and were handed back to be executed again on their own. Release
    /// their pending counts now and take them out of the batch, whose terminal must neither count nor settle them
    /// </summary>
    public static void HandBack(IWolverineRuntime runtime, Envelope batchEnvelope, IReadOnlyList<Envelope> members)
    {
        if (members.Count == 0) return;

        (runtime as WolverineRuntime)?.BatchingPendingCounts.SettleMembers(batchEnvelope, members);
        Detach(batchEnvelope, members);

        foreach (var member in members)
        {
            member.InBatch = false;
        }
    }

    // Array.CreateInstance closes over the runtime batch element type, same reflective shape as the rest
    // of the batching subsystem (DefaultMessageBatcher / BatchingOptions). AOT-clean apps supply their
    // own IMessageBatcher and run pre-generated handlers in TypeLoadMode.Static - see the AOT guide.
    /// <summary>
    /// Replay these original members as a reduced batch. Take them out of <paramref name="batchEnvelope"/> with
    /// <see cref="Detach"/> first.
    /// </summary>
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Reduced-batch array closed over runtime element type; AOT consumers register batchers explicitly. See AOT guide.")]
    public static async ValueTask EnqueueReducedBatchAsync(IWolverineRuntime runtime, Envelope batchEnvelope,
        IReadOnlyList<Envelope> members)
    {
        if (members.Count == 0)
        {
            return;
        }

        var destination = batchEnvelope.Destination
            ?? throw new InvalidOperationException(
                "The batch envelope has no Destination local queue to replay a reduced batch onto");

        if (runtime.Endpoints.AgentForLocalQueue(destination) is not ILocalQueue queue)
        {
            throw new InvalidOperationException(
                $"Unable to resolve local queue '{destination}' to replay a reduced batch onto");
        }

        // Rebuild the typed T[] the batch handler expects from the batch message's element type.
        var elementType = batchEnvelope.Message?.GetType().GetElementType() ?? members[0].Message!.GetType();
        var typedArray = Array.CreateInstance(elementType, members.Count);
        for (var i = 0; i < members.Count; i++)
        {
            // The members carry their items BY REFERENCE, so a subsequent ApplyItemException on the replayed
            // batch can still map thrown items back to their members.
            typedArray.SetValue(members[i].Message, i);
        }

        var reduced = new Envelope(typedArray, members)
        {
            Destination = destination,
            MessageType = batchEnvelope.MessageType,
            TenantId = batchEnvelope.TenantId,

            // GH-3867 — carry the group id, or the reduced batch draws a RANDOM partition slot on the
            // way back in and a poison probe scatters the original batch's items across slots.
            GroupId = batchEnvelope.GroupId,

            // GH-4397 / F8 -- the members' pending counts come with them from the original batch, which no longer
            // holds them, so the reduced batch releases exactly what the original would have
            BatchPipelineCounted = batchEnvelope.BatchPipelineCounted
        };

        try
        {
            await queue.EnqueueAsync(reduced).ConfigureAwait(false);
        }
        catch
        {
            // Nothing will run them now. Their inbox rows, if they have them, are left exactly as they are, still
            // owned by this node, to be recovered; settling them here would lose them.
            (runtime as WolverineRuntime)?.BatchingPendingCounts.SettleBatch(reduced);
            foreach (var member in members)
            {
                member.InBatch = false;
            }

            throw;
        }
    }

    private static bool containsByReference(IReadOnlyCollection<Envelope> members, Envelope member)
    {
        foreach (var candidate in members)
        {
            if (ReferenceEquals(candidate, member)) return true;
        }

        return false;
    }
}
