using Bobcat;
using Bobcat.Xunit;
using DurableComplianceRig.Infrastructure;
using Wolverine;

namespace DurableComplianceRig.Scenarios;

/// <summary>
/// A listener that pauses, is rebuilt, or cannot write its reschedule, while a handler is still running on it. The
/// question every spec asks is the same: was the running handler's inbox row ever handed back to the cluster while
/// it ran, and did anything run it a second time at the same moment? Store and transport agnostic; each concrete
/// subclass is bodyless and names its own feature.
/// </summary>
public abstract class ListenerDisruptionCompliance : IAsyncLifetime
{
    protected readonly DurableSteps the;

    protected ListenerDisruptionCompliance(StoreProfile store, TransportProfile transport, ITestOutputHelper output)
    {
        the = new DurableSteps(store, transport, output);
    }

    public ValueTask InitializeAsync() => new(the.PrepareLifecycleAsync());

    public async ValueTask DisposeAsync()
    {
        await the.BuryKilledNodesAsync();
        await the.DisposeAsync();
    }

    /// <summary>
    /// F15. A pause raised from inside the handler pipeline cannot wait for its own execution, so it drains without
    /// latching -- and the drain's release then runs underneath every sibling handler still executing.
    /// </summary>
    [BobcatSpec]
    public async Task a_pause_raised_by_a_failure_does_not_release_a_sibling_still_running()
    {
        the.Mode = DurabilityMode.Balanced;
        the.TheClusterReactsWithinSeconds();
        the.ListenersRunInParallel(4);
        the.MessagesToSend(3);
        the.AFailureRequeuesAndPauses(2, 2);
        await the.ANodeStarts();
        the.MessageWaitsForGate(1, "a");
        the.MessageFailsFirst(2, 1);

        await the.TheMessageIsSent(1, 1);
        await the.MessageHasStarted(1);
        the.TheInboxRowIsWatched(1);
        await the.TheMessageIsSent(2, 1);
        await the.AttemptHasStarted(1, 2);
        await the.ANodeStarts();
        await the.UpToSecondsPassForARerun(15, 1);
        the.GateOpens("a");
        await the.TheMessageIsSent(3, 1);

        await the.TheDurableInvariantsHold();
        await the.TheRowStayedOwnedWhileItsHandlerRan(1);
    }

    /// <summary>
    /// F16. A forced rebuild drains with a latch, so GH-4797 holds the running handler's row -- and then the new
    /// receiver's ClearInFlightIncomingAsync releases it anyway.
    /// </summary>
    [BobcatSpec]
    public async Task a_rebuilt_listener_does_not_release_a_row_still_running()
    {
        the.TheClusterReactsWithinSeconds();
        the.TheDrainTimeoutIs(1000);
        the.ListenersRunInParallel(4);
        await the.ANodeStarts();
        the.MessagesToSend(3);
        the.MessageWaitsForGate(1, "a");

        await the.TheMessageIsSent(1, 1);
        await the.MessageHasStarted(1);
        the.TheInboxRowIsWatched(1);
        await the.TheListenerIsRebuilt(1);
        await the.UpToSecondsPassForARerun(15, 1);
        the.GateOpens("a");
        await the.TheMessageIsSent(2, 1);
        await the.TheMessageIsSent(3, 1);

        await the.TheDurableInvariantsHold();
        await the.TheRowStayedOwnedWhileItsHandlerRan(1);
    }

    /// <summary>
    /// F15's decisive pair, with the pipeline taken out: the same pause from outside, once without waiting for
    /// running handlers (what a pause raised from inside the pipeline does) and once after draining them (what the
    /// circuit breaker does). Only the first should hand the running handler's row back. The pause is long, so no
    /// restart (and so no F16) happens inside the scenario.
    /// </summary>
    [BobcatSpec]
    public async Task a_pause_that_does_not_wait_does_not_release_a_row_still_running()
    {
        the.Mode = DurabilityMode.Balanced;
        the.TheClusterReactsWithinSeconds();
        the.ListenersRunInParallel(4);
        await the.ANodeStarts();
        the.MessagesToSend(2);
        the.MessageWaitsForGate(1, "a");

        await the.TheMessageIsSent(1, 1);
        await the.MessageHasStarted(1);
        the.TheInboxRowIsWatched(1);
        await the.TheListenerPausesWithoutWaiting(1, 60);
        await the.ANodeStarts();
        await the.UpToSecondsPassForARerun(15, 1);
        the.GateOpens("a");
        await the.TheMessageIsSent(2, 2);

        await the.TheDurableInvariantsHold();
        await the.TheRowStayedOwnedWhileItsHandlerRan(1);
    }

    [BobcatSpec]
    public async Task a_pause_that_drains_first_holds_a_row_still_running()
    {
        the.Mode = DurabilityMode.Balanced;
        the.TheClusterReactsWithinSeconds();
        the.TheDrainTimeoutIs(1000);
        the.ListenersRunInParallel(4);
        await the.ANodeStarts();
        the.MessagesToSend(2);
        the.MessageWaitsForGate(1, "a");

        await the.TheMessageIsSent(1, 1);
        await the.MessageHasStarted(1);
        the.TheInboxRowIsWatched(1);
        await the.TheListenerPausesAfterDraining(1, 60);
        await the.ANodeStarts();
        await the.UpToSecondsPassForARerun(15, 1);
        the.GateOpens("a");
        await the.TheMessageIsSent(2, 2);

        await the.TheDurableInvariantsHold();
        await the.TheRowStayedOwnedWhileItsHandlerRan(1);
    }

    /// <summary>
    /// F16's decisive half: a rebuild split in two. Stopping and draining alone holds the running handler's row
    /// (GH-4797); starting again while it still runs is what releases it.
    /// </summary>
    [BobcatSpec]
    public async Task a_stopped_listener_holds_a_row_still_running_until_it_starts_again()
    {
        the.Mode = DurabilityMode.Balanced;
        the.TheClusterReactsWithinSeconds();
        the.TheDrainTimeoutIs(1000);
        the.ListenersRunInParallel(4);
        await the.ANodeStarts();
        the.MessagesToSend(2);
        the.MessageWaitsForGate(1, "a");

        await the.TheMessageIsSent(1, 1);
        await the.MessageHasStarted(1);
        the.TheInboxRowIsWatched(1);
        await the.TheListenerStopsAndDrains(1);
        await the.ANodeStarts();
        await the.UpToSecondsPassForARerun(10, 1);
        the.GateOpens("a");
        await the.SecondsPass(2);
        await the.TheListenerStarts(1);
        await the.TheMessageIsSent(2, 2);

        await the.TheDurableInvariantsHold();
        await the.TheRowStayedOwnedWhileItsHandlerRan(1);
    }

    /// <summary>
    /// F17. A scheduled retry that comes due while its listener is paused is claimed by this node and handed to a
    /// receiver that is not there. A durable local queue returns without a word, so nothing ever releases the row.
    /// </summary>
    [BobcatSpec]
    public async Task a_retry_coming_due_while_its_listener_is_paused_is_not_dropped()
    {
        the.SettleTimeout = 45.Seconds();
        the.TheClusterReactsWithinSeconds();
        the.TheDrainTimeoutIs(2000);
        the.MessagesToSend(3);
        the.AFailureIsScheduledForRetry(2, 4);
        the.AFailureRequeuesAndPauses(3, 8);
        await the.ANodeStarts();
        the.MessageFailsFirst(2, 1);
        the.MessageFailsFirst(3, 1);

        await the.TheMessagesAreSent(1);

        await the.TheDurableInvariantsHold();
    }

    /// <summary>F17's control: the same retry, coming due after the listener has resumed.</summary>
    [BobcatSpec]
    public async Task a_retry_coming_due_after_its_listener_resumes_settles()
    {
        the.SettleTimeout = 45.Seconds();
        the.TheClusterReactsWithinSeconds();
        the.TheDrainTimeoutIs(2000);
        the.MessagesToSend(3);
        the.AFailureIsScheduledForRetry(2, 16);
        the.AFailureRequeuesAndPauses(3, 8);
        await the.ANodeStarts();
        the.MessageFailsFirst(2, 1);
        the.MessageFailsFirst(3, 1);

        await the.TheMessagesAreSent(1);

        await the.TheDurableInvariantsHold();
    }

    /// <summary>
    /// A pause raised from inside the handler pipeline of a durable local queue drains with a latch, so it waits for
    /// its own execution until DrainTimeout gives up. A two second pause then lasts DrainTimeout plus two seconds.
    /// </summary>
    [BobcatSpec]
    public async Task a_two_second_pause_raised_by_a_failure_lasts_about_two_seconds()
    {
        the.TheClusterReactsWithinSeconds();
        the.MessagesToSend(2);
        the.AFailureRequeuesAndPauses(1, 2);
        await the.ANodeStarts();
        the.MessageFailsFirst(1, 1);

        await the.TheMessagesAreSent(1);

        await the.TheDurableInvariantsHold();
        the.AttemptStartedWithin(2, 1, 10);
    }

    /// <summary>The control for the pause above: if the stall is the drain waiting on itself, DrainTimeout sets it.</summary>
    [BobcatSpec]
    public async Task a_two_second_pause_raised_by_a_failure_with_a_five_second_drain_timeout_lasts_about_seven()
    {
        the.TheClusterReactsWithinSeconds();
        the.TheDrainTimeoutIs(5000);
        the.MessagesToSend(2);
        the.AFailureRequeuesAndPauses(1, 2);
        await the.ANodeStarts();
        the.MessageFailsFirst(1, 1);

        await the.TheMessagesAreSent(1);

        await the.TheDurableInvariantsHold();
        the.AttemptStartedWithin(2, 1, 10);
    }

    /// <summary>The control for the blip below: the same policy and script with a healthy database.</summary>
    [BobcatSpec]
    public async Task a_message_retried_inline_and_then_scheduled_settles()
    {
        the.TheClusterReactsWithinSeconds();
        the.MessagesToSend(3);
        the.AFailureIsRetriedInlineThenScheduled(1, 1500, 2);
        await the.ANodeStarts();
        the.MessageFailsFirst(1, 2);

        await the.TheMessagesAreSent(1);

        await the.TheDurableInvariantsHold();
    }

    /// <summary>
    /// The F12 control: a blip the scheduled-retry write's retry budget can ride out. If this fails too, the blip
    /// itself is the problem rather than its length.
    /// </summary>
    [BobcatSpec]
    public async Task a_short_database_blip_while_scheduling_a_retry_is_ridden_out()
    {
        the.TheClusterReactsWithinSeconds();
        the.MessagesToSend(3);
        the.AFailureIsRetriedInlineThenScheduled(1, 1500, 2);
        await the.ANodeStarts();
        the.MessageFailsFirst(1, 2);

        await the.TheMessagesAreSent(1);
        await the.TheDatabaseCutsWritesToTheRow(1, 100);

        await the.TheDurableInvariantsHold();
    }

    /// <summary>
    /// F12. The scheduled-retry write runs through a RetryBlock with the default budget of three attempts. A blip
    /// longer than that budget would leave the row Incoming and owned by this live node, which nothing reclaims.
    /// </summary>
    [BobcatSpec]
    public async Task a_database_blip_while_scheduling_a_retry_does_not_strand_the_message()
    {
        the.SettleTimeout = 45.Seconds();
        the.TheClusterReactsWithinSeconds();
        the.MessagesToSend(3);
        the.AFailureIsRetriedInlineThenScheduled(1, 1500, 2);
        await the.ANodeStarts();
        the.MessageFailsFirst(1, 2);

        await the.TheMessagesAreSent(1);
        await the.TheDatabaseCutsWritesToTheRow(1, 3000);

        await the.TheDurableInvariantsHold();
    }

    /// <summary>
    /// F12's mechanism, from the other side: the stranded row is Incoming and owned by a live node, which only
    /// InboxStaleTime reclaims. With it set, the same blip is recovered.
    /// </summary>
    [BobcatSpec]
    public async Task a_database_blip_while_scheduling_a_retry_is_recovered_by_the_inbox_stale_time()
    {
        the.SettleTimeout = 45.Seconds();
        the.TheClusterReactsWithinSeconds();
        the.TheInboxStaleTimeIs(3);
        the.MessagesToSend(3);
        the.AFailureIsRetriedInlineThenScheduled(1, 1500, 2);
        await the.ANodeStarts();
        the.MessageFailsFirst(1, 2);

        await the.TheMessagesAreSent(1);
        await the.TheDatabaseCutsWritesToTheRow(1, 3000);

        await the.TheDurableInvariantsHold();
    }
}
