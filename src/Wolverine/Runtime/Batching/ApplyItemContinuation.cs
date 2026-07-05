using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports.Local;

namespace Wolverine.Runtime.Batching;

/// <summary>
/// Built-in continuation source for <see cref="ApplyItemException"/>. Registered as an indefinite
/// (every-attempt) failure rule on the exception type in the <c>WolverineOptions</c> constructor, so
/// throwing the exception from a batch handler is itself the opt-in. See GH-3289.
/// </summary>
internal class ApplyItemContinuationSource : IContinuationSource
{
    public string Description => "Isolate the poison item(s) named by ApplyItemException from the batch";

    public IContinuation Build(Exception ex, Envelope envelope)
    {
        // The rule only matches ApplyItemException; the fallback is purely defensive.
        return ex is ApplyItemException apply
            ? new ApplyItemContinuation(apply)
            : new MoveToErrorQueue(ex);
    }
}

/// <summary>
/// Resolves an <see cref="ApplyItemException"/> thrown by a batch handler: dead-letters only the poison
/// member envelopes, dispositions the survivors (ack and/or replay), and settles the original batch.
/// </summary>
internal class ApplyItemContinuation : IContinuation
{
    private readonly ApplyItemException _exception;

    public ApplyItemContinuation(ApplyItemException exception)
    {
        _exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    public async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime,
        DateTimeOffset now, Activity? activity)
    {
        var batch = lifecycle.Envelope;
        var members = batch?.Batch;

        // Defensive: ApplyItemException thrown from something that is not actually a batch. Nothing to
        // isolate, so fall back to dead-lettering the whole envelope.
        if (batch is null || members is null || members.Length == 0)
        {
            await lifecycle.MoveToDeadLetterQueueAsync(_exception).ConfigureAwait(false);
            await lifecycle.CompleteAsync().ConfigureAwait(false);
            if (batch is not null)
            {
                runtime.MessageTracking.MessageFailed(batch, _exception);
            }

            return;
        }

        // Partition members by reference identity: a thrown poison item is matched to the member
        // envelope whose Message IS that instance. This is exact for a normal (non-coalesced) batch.
        // NOTE (GH-3289): under CoalesceBy the handler sees the last-wins instance for a key, which is
        // the Message of only ONE member, so only that member is dead-lettered here; the earlier
        // same-key members fall into the survivor set (acked/replayed). Poisoning every member that
        // collapsed into a coalesced key is a deliberate follow-up (items 2/3).
        var poison = members.Where(m => containsByReference(_exception.PoisonItems, m.Message)).ToArray();
        var survivors = members.Where(m => !poison.Contains(m)).ToArray();

        var replay = _exception.Disposition switch
        {
            NonPoisonItems.AckAll => Array.Empty<Envelope>(),
            NonPoisonItems.Replay => survivors,
            NonPoisonItems.AckSelected => survivors
                .Where(m => !containsByReference(_exception.AckItems, m.Message)).ToArray(),
            _ => survivors
        };

        // 1. Dead-letter ONLY the poison members (the final CompleteAsync then settles them too).
        if (poison.Length > 0)
        {
            if (lifecycle is MessageContext context)
            {
                await context.MoveBatchMembersToDeadLetterQueueAsync(poison, _exception).ConfigureAwait(false);
            }
            else
            {
                // No fine-grained control on this lifecycle -> fall back to whole-batch dead-lettering.
                await lifecycle.MoveToDeadLetterQueueAsync(_exception).ConfigureAwait(false);
            }
        }

        // 2. Replay the survivors we are re-running as a fresh, reduced batch on the same local queue.
        if (replay.Length > 0)
        {
            var items = new object[replay.Length];
            for (var i = 0; i < replay.Length; i++)
            {
                items[i] = replay[i].Message!;
            }

            await BatchReplay.EnqueueReducedBatchAsync(runtime, batch, items).ConfigureAwait(false);
        }

        // 3. Settle every ORIGINAL member (ack survivors, the now-dead-lettered poison, and the
        //    replayed originals whose work now rides on the fresh reduced batch) plus the batch itself.
        await lifecycle.CompleteAsync().ConfigureAwait(false);

        runtime.MessageTracking.MessageFailed(batch, _exception);
        activity?.AddEvent(new ActivityEvent("Wolverine.Batch.ItemsIsolated"));
    }

    private static bool containsByReference(IReadOnlyList<object> items, object? message)
    {
        if (message is null)
        {
            return false;
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], message))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Shared re-execution primitive for the batch item-isolation features (GH-3289): re-enqueue a reduced
/// batch (a subset of the original items) onto the batch's own local queue so the batch handler runs
/// again over only those items. No separate single-element handler is needed.
/// </summary>
internal static class BatchReplay
{
    // Array.CreateInstance closes over the runtime batch element type, same reflective shape as the rest
    // of the batching subsystem (DefaultMessageBatcher / BatchingOptions). AOT-clean apps supply their
    // own IMessageBatcher and run pre-generated handlers in TypeLoadMode.Static - see the AOT guide.
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Reduced-batch array closed over runtime element type; AOT consumers register batchers explicitly. See AOT guide.")]
    public static async ValueTask EnqueueReducedBatchAsync(IWolverineRuntime runtime, Envelope batchEnvelope,
        IReadOnlyList<object> items)
    {
        if (items.Count == 0)
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
        var elementType = batchEnvelope.Message?.GetType().GetElementType() ?? items[0].GetType();
        var typedArray = Array.CreateInstance(elementType, items.Count);
        var members = new Envelope[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            typedArray.SetValue(items[i], i);

            // Fresh member envelopes carry the items BY REFERENCE so a subsequent ApplyItemException on
            // the replayed batch can still map thrown items back to their members.
            members[i] = new Envelope(items[i]);
        }

        var reduced = new Envelope(typedArray, members)
        {
            Destination = destination,
            MessageType = batchEnvelope.MessageType,
            TenantId = batchEnvelope.TenantId
        };

        await queue.EnqueueAsync(reduced).ConfigureAwait(false);
    }
}
