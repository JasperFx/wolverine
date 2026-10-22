using Bobcat;
using Bobcat.Xunit;
using DurableComplianceRig.Infrastructure;

namespace DurableComplianceRig.Scenarios;

/// <summary>
/// <c>BatchMessagesOf&lt;RigItem&gt;()</c> on durable endpoints. Every member of a batch is a durable message in its
/// own right, with its own inbox row, so the same invariants judge it: one effect or one dead letter, a settled row.
/// Store-agnostic; each concrete subclass is bodyless and names its store and topology.
/// </summary>
/// <remarks>
/// <para>
/// Two topologies. Items sent to a durable local queue that the batches also execute on; and items arriving on a
/// durable broker listener, batched on the element type's default local queue, which is buffered unless made durable.
/// </para>
/// <para>
/// Every item is sent within the batch trigger time, so a scenario's items ride ONE batch and a scripted failure
/// of one member decides the fate of all of them. That is why a whole failing batch drags every item to the dead
/// letter queue, and why only the item-isolation features dead letter just the poison member.
/// </para>
/// </remarks>
public abstract class BatchingCompliance : IAsyncLifetime
{
    protected readonly DurableSteps the;

    protected BatchingCompliance(StoreProfile store, TransportProfile itemTransport, bool durableBatchQueue,
        ITestOutputHelper output)
    {
        the = new DurableSteps(store, new BatchedTransport(itemTransport, durableBatchQueue), output);
        BatchItemScript.Reset();
        the.CaptureErrorLogs();

        // A batch scenario that cannot settle is usually stuck for good; don't spend the default minute on it
        if (Environment.GetEnvironmentVariable("RIG_SETTLE_SECONDS") == null) the.SettleTimeout = 30.Seconds();
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await the.RestoreBatchingStateAsync();
        await the.DisposeAsync();
    }

    // ---------------------------------------------------------------------------------------------------------
    // The error policies

    [BobcatSpec]
    public async Task every_batched_item_commits_once_and_settles()
    {
        await the.ANodeStarts();
        the.MessagesToSend(20);

        await the.TheMessagesAreSentAsItems(1);

        await the.TheDurableInvariantsHold();
        the.NoBatchMembersArePending();
        the.NoErrorsSettlingBatchMembers();
    }

    [BobcatSpec]
    public async Task a_batch_that_fails_once_is_retried_on_a_schedule_and_settles()
    {
        the.FailedBatchesAreScheduledForRetry(2, 500);
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageFailsFirst(3, 1);

        await the.TheMessagesAreSentAsItems(1);

        await the.TheDurableInvariantsHold();
        the.NoBatchMembersArePending();
        the.NoErrorsSettlingBatchMembers();
    }

    [BobcatSpec]
    public async Task a_batch_that_always_fails_exhausts_its_scheduled_retries_and_dead_letters_every_member()
    {
        the.FailedBatchesAreScheduledForRetry(2, 500);
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageAlwaysFails(3);
        the.EveryItemIsDeadLetteredWithTheBatch();

        await the.TheMessagesAreSentAsItems(1);

        await the.TheDurableInvariantsHold();
        the.NoBatchMembersArePending();
        the.NoErrorsSettlingBatchMembers();
    }

    [BobcatSpec]
    public async Task a_batch_that_fails_once_is_retried_with_a_cooldown_and_settles()
    {
        the.FailedBatchesAreRetriedWithCooldown(2, 500);
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageFailsFirst(3, 1);

        await the.TheMessagesAreSentAsItems(1);

        await the.TheDurableInvariantsHold();
        the.NoBatchMembersArePending();
        the.NoErrorsSettlingBatchMembers();
    }

    [BobcatSpec]
    public async Task a_batch_that_fails_once_is_requeued_and_settles()
    {
        the.FailedBatchesAreRequeued(3);
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageFailsFirst(3, 1);

        await the.TheMessagesAreSentAsItems(1);

        await the.TheDurableInvariantsHold();
        the.NoBatchMembersArePending();
        the.NoErrorsSettlingBatchMembers();
    }

    [BobcatSpec]
    public async Task a_batch_that_always_fails_is_requeued_then_dead_letters_every_member()
    {
        the.FailedBatchesAreRequeued(3);
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageAlwaysFails(3);
        the.EveryItemIsDeadLetteredWithTheBatch();

        await the.TheMessagesAreSentAsItems(1);

        await the.TheDurableInvariantsHold();
        the.NoBatchMembersArePending();
        the.NoErrorsSettlingBatchMembers();
    }

    [BobcatSpec]
    public async Task a_batch_moved_to_the_error_queue_dead_letters_every_member()
    {
        the.FailedBatchesAreDeadLettered();
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageAlwaysFails(3);
        the.EveryItemIsDeadLetteredWithTheBatch();

        await the.TheMessagesAreSentAsItems(1);

        await the.TheDurableInvariantsHold();
        the.NoBatchMembersArePending();
        the.NoErrorsSettlingBatchMembers();
    }

    [BobcatSpec]
    public async Task a_discarded_batch_settles_every_member_without_an_effect()
    {
        the.FailedBatchesAreDiscarded();
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageFailsFirst(3, 1);
        the.EveryItemIsDiscardedWithTheBatch();

        await the.TheMessagesAreSentAsItems(1);

        await the.TheDurableInvariantsHold();
        the.NoBatchMembersArePending();
        the.NoErrorsSettlingBatchMembers();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Item isolation

    [BobcatSpec]
    public async Task probing_members_individually_dead_letters_only_the_poison_member()
    {
        the.FailedBatchesAreProbedIndividually(2);
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageAlwaysFails(3);

        await the.TheMessagesAreSentAsItems(1);

        await the.TheDurableInvariantsHold();
        the.NoBatchMembersArePending();
        the.NoErrorsSettlingBatchMembers();
    }

    [BobcatSpec]
    public async Task isolating_members_dead_letters_only_the_poison_member()
    {
        the.FailedBatchesIsolateTheirMembers();
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageAlwaysFails(3);

        await the.TheMessagesAreSentAsItems(1);

        await the.TheDurableInvariantsHold();
        the.NoBatchMembersArePending();
        the.NoErrorsSettlingBatchMembers();
    }

    [BobcatSpec]
    public async Task an_apply_item_exception_dead_letters_only_the_poison_member_and_replays_the_rest()
    {
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.ItemIsPoisonForApplyItem(3);

        await the.TheMessagesAreSentAsItems(1);

        await the.TheDurableInvariantsHold();
        the.NoBatchMembersArePending();
        the.NoErrorsSettlingBatchMembers();
    }

    // ---------------------------------------------------------------------------------------------------------
    // Leads from the findings ledger

    /// <summary>The control for the F1 crash scenario below: an ordinary batch caught by a crash runs again.</summary>
    [BobcatSpec]
    public async Task a_batch_interrupted_by_a_crash_runs_again_on_restart()
    {
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageWaitsForGate(1, "held");

        await the.TheMessagesAreSentAsItems(1);
        await the.MessageHasStarted(1);
        the.NodeCrashesMidBatch(1);
        await the.ANodeStarts();

        await the.TheDurableInvariantsHold();
        the.NoBatchMembersArePending();
    }

    /// <summary>
    /// F1. The survivors of an ApplyItemException ride a fresh in-memory batch, while their original rows are marked
    /// Handled before it runs. A crash before the replay finishes should leave them to be recovered, not lost.
    /// </summary>
    [BobcatSpec]
    public async Task apply_item_survivors_still_waiting_on_their_replay_survive_a_crash()
    {
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.ItemIsPoisonForApplyItem(3);

        // The poison batch throws before any member begins, so item 1's first run is its replay
        the.MessageWaitsForGate(1, "held");

        await the.TheMessagesAreSentAsItems(1);
        await the.MessageHasStarted(1);
        the.NodeCrashesMidBatch(1);
        await the.ANodeStarts();

        await the.TheDurableInvariantsHold();
        the.NoBatchMembersArePending();
    }

    /// <summary>
    /// F2. The poison member's dead letter write fails. It should be executed again later, and the survivors replayed
    /// as usual -- not every member marked Handled because the failed write escaped the continuation.
    /// </summary>
    [BobcatSpec]
    public async Task an_apply_item_poison_whose_dead_letter_write_fails_loses_nothing()
    {
        // The failed dead letter write is redelivered after 30 seconds
        the.SettleTimeout = 90.Seconds();
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.ItemIsPoisonForApplyItem(3);
        await the.DeadLetterStorageRefusesWrites();

        await the.TheMessagesAreSentAsItems(1);
        await the.ThePoisonItemHasBeenNamed();
        await the.SecondsPass(8);
        await the.DeadLetterStorageAcceptsWrites();

        await the.TheDurableInvariantsHold();
        the.NoBatchMembersArePending();
        the.NoErrorsSettlingBatchMembers();
    }

    /// <summary>F2, through ProbeIndividuallyAfter: the poison singleton's dead letter write fails.</summary>
    [BobcatSpec]
    public async Task a_probed_poison_member_whose_dead_letter_write_fails_loses_nothing()
    {
        the.SettleTimeout = 90.Seconds();
        the.FailedBatchesAreProbedIndividually(1);
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageAlwaysFails(3);
        await the.DeadLetterStorageRefusesWrites();

        await the.TheMessagesAreSentAsItems(1);
        await the.MessageHasStarted(3);
        await the.SecondsPass(10);
        await the.DeadLetterStorageAcceptsWrites();

        await the.TheDurableInvariantsHold();
        the.NoBatchMembersArePending();
        the.NoErrorsSettlingBatchMembers();
    }

    /// <summary>The control for F2: a whole batch whose dead letter write fails is executed again and dead lettered later.</summary>
    [BobcatSpec]
    public async Task a_batch_whose_dead_letter_write_fails_is_dead_lettered_later()
    {
        the.SettleTimeout = 90.Seconds();
        the.FailedBatchesAreDeadLettered();
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageAlwaysFails(3);
        the.EveryItemIsDeadLetteredWithTheBatch();
        await the.DeadLetterStorageRefusesWrites();

        await the.TheMessagesAreSentAsItems(1);
        await the.MessageHasStarted(3);

        // Each member's write is tried for about 3.3 seconds, one member after another: refuse long enough that
        // every member's write fails, so all five come back together and fail together
        await the.SecondsPass(25);
        await the.DeadLetterStorageAcceptsWrites();

        await the.TheDurableInvariantsHold();
        the.NoBatchMembersArePending();
        the.NoErrorsSettlingBatchMembers();
    }

    /// <summary>
    /// F3. Every item settled; a graceful restart must find nothing to run again. Members whose rows were never marked
    /// Handled are released by the stop and recovered by the next node, which runs them a second time.
    /// </summary>
    [BobcatSpec]
    public async Task a_restart_after_every_batch_settled_runs_nothing_again()
    {
        await the.ANodeStarts();
        the.MessagesToSend(10);

        await the.TheMessagesAreSentAsItems(1);
        await the.EveryHandledItemHasCommitted();
        await the.SecondsPass(2);
        await the.NodeStops(1);
        await the.ANodeStarts();
        await the.SecondsPass(12);

        await the.TheDurableInvariantsHold();
        await the.NoItemCommittedTwice();
    }

    /// <summary>
    /// F4. The members' rows are owned at the listener they arrived on. Pausing that listener releases them while
    /// their batch's scheduled retry is still pending in memory, so recovery and the retry can both run them.
    /// </summary>
    [BobcatSpec]
    public async Task pausing_the_item_listener_while_a_batch_retry_is_pending_runs_nothing_twice()
    {
        the.FailedBatchesAreScheduledForRetry(1, 4000);
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageFailsFirst(3, 1);

        await the.TheMessagesAreSentAsItems(1);
        await the.MessageHasStarted(3);
        await the.SecondsPass(1);
        await the.TheItemListenerPauses(1, 1000);

        await the.TheDurableInvariantsHold();
        the.NoBatchMembersArePending();
        the.NoErrorsSettlingBatchMembers();
    }

    /// <summary>F4, the batch queue's side: its drain cancels the pending retry; the members must still settle.</summary>
    [BobcatSpec]
    public async Task pausing_the_batch_queue_while_a_batch_retry_is_pending_loses_nothing()
    {
        the.FailedBatchesAreScheduledForRetry(1, 4000);
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageFailsFirst(3, 1);

        await the.TheMessagesAreSentAsItems(1);
        await the.MessageHasStarted(3);
        await the.SecondsPass(1);
        await the.TheBatchQueuePauses(1, 1000);

        await the.TheDurableInvariantsHold();
        the.NoBatchMembersArePending();
        the.NoErrorsSettlingBatchMembers();
    }

    /// <summary>
    /// F6. Items wait in the batching channel for the trigger time; the batch queue pauses meanwhile. The assembled
    /// batch is then enqueued into a paused queue.
    /// </summary>
    [BobcatSpec]
    public async Task pausing_the_batch_queue_while_items_wait_to_be_batched_loses_nothing()
    {
        await the.ANodeStarts();
        the.MessagesToSend(5);

        await the.TheMessagesAreSentAsItems(1);
        await the.TheBatchQueuePauses(1, 3000);

        await the.TheDurableInvariantsHold();
        the.NoBatchMembersArePending();
        the.NoErrorsSettlingBatchMembers();
    }

    /// <summary>
    /// F5. The InboxStaleTime heartbeat restarts the clock of what the receiver holds -- the batch carrier, which has
    /// no row -- so a slow batch's member rows go stale and are released while the batch is still running.
    /// </summary>
    [BobcatSpec]
    public async Task a_slow_batch_is_not_released_by_the_stale_sweep()
    {
        the.InboxStaleTimeIs(1500);
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageIsSlow(1, 15000);

        await the.TheMessagesAreSentAsItems(1);

        await the.TheDurableInvariantsHold();
        the.NoBatchMembersArePending();
        the.NoErrorsSettlingBatchMembers();
    }

    /// <summary>
    /// The control for F5: the same slow handler and stale time, but the messages sent singly, unbatched, over the
    /// same endpoint. The heartbeat holds their rows, so nothing is released.
    /// </summary>
    [BobcatSpec]
    public async Task a_slow_unbatched_message_on_the_same_endpoint_is_not_released_by_the_stale_sweep()
    {
        the.InboxStaleTimeIs(1500);
        await the.ANodeStarts();
        the.MessagesToSend(5);
        the.MessageIsSlow(1, 15000);

        await the.TheMessagesAreSent(1);

        await the.TheDurableInvariantsHold();
    }
}
