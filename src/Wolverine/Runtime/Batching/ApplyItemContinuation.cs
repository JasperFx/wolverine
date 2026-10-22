using System.Diagnostics;
using Wolverine.ErrorHandling;

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
            if (!await BatchDeadLetters.TryMoveToDeadLetterQueueAsync(lifecycle, runtime, _exception)
                    .ConfigureAwait(false))
            {
                return;
            }

            await lifecycle.CompleteAsync().ConfigureAwait(false);
            if (batch is not null)
            {
                runtime.MessageTracking.MessageFailed(batch, _exception);
            }

            return;
        }

        // Partition members by reference identity: a thrown poison item is matched to the member envelope
        // whose Message IS that instance. This is exact for a normal (non-coalesced) batch.
        var poison = members.Where(m => containsByReference(_exception.PoisonItems, m.Message)).ToList();

        // GH-3289: under CoalesceBy the handler only sees the last-wins instance for a key (the Message of
        // ONE member), but every member that collapsed into that key is "about" the same flagged item, so
        // poison them all. BatchGroupId is stamped per coalesced key by CoalescingMessageBatcher; it is
        // null for a normal batch, in which case this expansion is a no-op.
        var poisonGroups = poison.Where(m => m.BatchGroupId.HasValue).Select(m => m.BatchGroupId!.Value)
            .ToHashSet();
        if (poisonGroups.Count > 0)
        {
            foreach (var member in members)
            {
                if (member.BatchGroupId.HasValue && poisonGroups.Contains(member.BatchGroupId.Value)
                                                 && !poison.Contains(member))
                {
                    poison.Add(member);
                }
            }
        }

        var survivors = members.Where(m => !poison.Contains(m)).ToArray();

        var replay = _exception.Disposition switch
        {
            NonPoisonItems.AckAll => Array.Empty<Envelope>(),
            NonPoisonItems.Replay => survivors,
            NonPoisonItems.AckSelected => survivors
                .Where(m => !containsByReference(_exception.AckItems, m.Message)).ToArray(),
            _ => survivors
        };

        // 1. Dead-letter ONLY the poison members, each under its own identity
        if (poison.Count > 0)
        {
            if (lifecycle is MessageContext context)
            {
                // F2. A poison member whose dead-letter write failed has been handed back to be executed again on
                // its own. It must not be settled with this batch, and it must not stop the survivors being replayed
                var handedBack = await context.MoveBatchMembersToDeadLetterQueueAsync(poison, _exception)
                    .ConfigureAwait(false);

                BatchReplay.HandBack(runtime, batch, handedBack);
            }
            else
            {
                // No fine-grained control on this lifecycle -> fall back to whole-batch dead-lettering.
                await lifecycle.MoveToDeadLetterQueueAsync(_exception).ConfigureAwait(false);
            }
        }

        // 2. Replay the survivors we are re-running as a fresh, reduced batch on the same local queue. F1: these are
        //    the ORIGINAL member envelopes, taken out of this batch so that each is settled only when its own replay
        //    reaches its terminal, and not here while the replay exists only in memory
        if (replay.Length > 0)
        {
            BatchReplay.Detach(batch, replay);
            await BatchReplay.EnqueueReducedBatchAsync(runtime, batch, replay).ConfigureAwait(false);
        }

        // 3. Settle the members left in this batch -- the dead-lettered poison and any survivors acked as they are --
        //    and the batch itself
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
