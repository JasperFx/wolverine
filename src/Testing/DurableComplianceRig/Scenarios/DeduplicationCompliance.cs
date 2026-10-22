using Bobcat.Xunit;
using DurableComplianceRig.Infrastructure;
using DurableComplianceRig.Stores;
using Wolverine;
using Wolverine.ErrorHandling;

namespace DurableComplianceRig.Scenarios;

/// <summary>
/// Logical deduplication (GH-4180, GH-4505, GH-4757, GH-5108) through the durable inbox: several copies of one
/// intent, each its own message and envelope, sharing one deduplication id. Run per store, transport and
/// <see cref="MessageDeduplicationMode" />.
/// </summary>
/// <remarks>
/// <para>
/// Three claim paths are in play, and the specs are written against what docs/guide/durability/idempotency.md says
/// each one promises. The plain stores claim on their own connection before the handler and release on a throw
/// ("compensated"). Marten queues the claim onto the session and checks optimistically, so a concurrent loser is
/// refused at commit. EF Core (Eager) INSERTs the claim on its own transaction before the handler, so a concurrent
/// copy waits on the uncommitted row.
/// </para>
/// </remarks>
public abstract class DeduplicationCompliance : IAsyncLifetime
{
    protected readonly DurableSteps the;
    private readonly MessageDeduplicationMode _mode;

    protected DeduplicationCompliance(StoreProfile store, TransportProfile transport, MessageDeduplicationMode mode,
        ITestOutputHelper output)
    {
        DeduplicationHooks.Reset();
        MartenRigHooks.Reset();
        _mode = mode;
        the = new DurableSteps(store, transport, output) { DeduplicationMode = mode };
        the.ConfigureNodes(opts =>
        {
            // Inline retries, then the default: dead letter
            opts.OnException<RigFailure>().RetryTimes(3);
            DeduplicationRig.Configure(opts, store, transport, the.QueueName, mode);
        });
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        DeduplicationHooks.Reset();
        MartenRigHooks.Reset();
        await the.DisposeAsync();
    }

    // ---------------------------------------------------------------------------------------------------------
    // 1. The basic promise

    [BobcatSpec]
    public async Task copies_of_one_intent_commit_one_effect()
    {
        await the.ANodeStarts();
        the.CopiesOfAnIntent(5, "order");
        the.CopiesOfAnIntent(1, "other-order");
        the.CopiesOfAnIntent(1, "third-order");

        await the.TheCopiesAreSent(1);
        await the.EveryCopyHasSettled();

        await the.TheDurableInvariantsHold();
        the.EveryIntentCommittedOneEffect();
        the.NoCopyWasDeadLettered();
        await the.TheStoreHoldsClaims(1, "order");
    }

    // ---------------------------------------------------------------------------------------------------------
    // 2. Concurrent copies

    /// <summary>
    /// Every copy sits in its handler for a while, so the copies of an intent overlap: on Marten they all pass the
    /// optimistic check and the commit has to refuse the losers; on EF Core the later copies wait on the first claim.
    /// </summary>
    [BobcatSpec]
    public async Task copies_handled_at_the_same_time_on_one_node_commit_one_effect()
    {
        await the.ANodeStarts();
        the.IntentsWithCopiesEach(6, 4);
        the.EveryCopyTakes(300);

        await the.TheCopiesAreSent(1);
        await the.EveryCopyHasSettled();

        await the.TheDurableInvariantsHold();
        the.EveryIntentCommittedOneEffect();
        the.NoCopyWasDeadLettered();
    }

    [BobcatSpec]
    public async Task copies_handled_at_the_same_time_on_two_balanced_nodes_commit_one_effect()
    {
        the.Mode = DurabilityMode.Balanced;
        await the.ANodeStarts();
        await the.ANodeStarts();
        the.IntentsWithCopiesEach(6, 4);
        the.EveryCopyTakes(300);

        await the.TheCopiesAreSentFromTwoNodes(1, 2);
        await the.EveryCopyHasSettled();

        await the.TheDurableInvariantsHold();
        the.EveryIntentCommittedOneEffect();
        the.NoCopyWasDeadLettered();
    }

    // ---------------------------------------------------------------------------------------------------------
    // 3. The claim and the handler's failure

    /// <summary>"Failed handlers do not poison the id": the dead-lettered first copy leaves the key free.</summary>
    [BobcatSpec]
    public async Task a_dead_lettered_copy_leaves_the_key_free_for_a_later_copy()
    {
        await the.ANodeStarts();
        the.CopiesOfAnIntent(2, "invoice");
        the.MessageAlwaysFails(1);
        the.MessageMustCommit(2);

        await the.CopyIsSent(1, 1);
        await the.MessageHasBeenDeadLettered(1);
        await the.CopyIsSent(2, 1);
        await the.EveryCopyHasSettled();

        await the.TheDurableInvariantsHold();
        the.TheIntentsCommittedEffects("invoice", 1);
        await the.TheStoreHoldsClaims(1, "invoice");
    }

    /// <summary>A retry is not refused as a duplicate of its own failed attempt, and a later copy is refused.</summary>
    [BobcatSpec]
    public async Task a_copy_that_fails_then_succeeds_on_retry_commits_and_holds_the_key()
    {
        await the.ANodeStarts();
        the.CopiesOfAnIntent(2, "payment");
        the.MessageFailsFirst(1, 2);
        the.MessageMustCommit(1);

        await the.CopyIsSent(1, 1);
        await the.MessageHasCommitted(1);
        await the.CopyIsSent(2, 1);
        await the.EveryCopyHasSettled();

        await the.TheDurableInvariantsHold();
        the.TheIntentsCommittedEffects("payment", 1);
        the.NoCopyWasDeadLettered();
    }

    /// <summary>
    /// A second copy arrives while the first holds the claim and is about to fail every attempt. Undocumented
    /// territory: the docs only promise that a failure gives the key back. Judged against the weakest promise worth
    /// making (one effect at most, and no silent nothing), and narrated so each store's answer can be compared.
    /// </summary>
    [BobcatSpec]
    public async Task a_copy_arriving_while_the_first_is_failing_is_not_silently_lost()
    {
        await the.ANodeStarts();
        the.CopiesOfAnIntent(2, "refund");
        the.CopyHoldsInItsHandler(1);
        the.CopyAlwaysFails(1);

        await the.CopyIsSent(1, 1);
        await the.CopyIsHolding(1);
        await the.CopyIsSent(2, 1);
        await the.TimePasses(2000);
        the.CopyIsLetGo(1);
        await the.EveryCopyHasSettled();

        await the.TheDurableInvariantsHold();
        the.TheIntentWasDoneOnceOrDeadLettered("refund");
    }

    // ---------------------------------------------------------------------------------------------------------
    // 4. The claim and the business transaction

    /// <summary>The effect is written and then the handler throws: the effect and the claim both go.</summary>
    [BobcatSpec]
    public async Task a_failure_after_the_effect_is_written_rolls_back_the_effect_and_the_claim()
    {
        await the.ANodeStarts();
        the.CopiesOfAnIntent(2, "shipment");
        the.MessageAlwaysFailsBeforeCommit(1);
        the.MessageMustCommit(2);

        await the.CopyIsSent(1, 1);
        await the.MessageHasBeenDeadLettered(1);
        await the.CopyIsSent(2, 1);
        await the.EveryCopyHasSettled();

        await the.TheDurableInvariantsHold();
        the.TheIntentsCommittedEffects("shipment", 1);
        await the.TheStoreHoldsClaims(1, "shipment");
    }

    /// <summary>
    /// Where the claim lives while the handler runs. The docs: committed up front on its own connection for the plain
    /// stores ("compensated"); inside the handler's transaction, invisible until it commits, on Marten and EF Core.
    /// </summary>
    [BobcatSpec]
    public async Task the_claim_is_inside_the_handlers_transaction_only_where_the_store_says_so()
    {
        await the.ANodeStarts();
        the.CopiesOfAnIntent(1, "audit");
        the.HandlersProbeTheirClaims();

        await the.TheCopiesAreSent(1);
        await the.EveryCopyHasSettled();

        await the.TheDurableInvariantsHold();
        the.TheClaimWasVisibleWhileRunning(1, the.Store.Semantics == EffectSemantics.AtLeastOnce ? "yes" : "no");
        await the.TheStoreHoldsClaims(1, "audit");
    }

    /// <summary>
    /// A crash after the first copy committed: the claim survives, and a copy sent to the node that takes over is
    /// refused. On the plain stores this also covers the copy itself being recovered and run again: its claim refuses
    /// it, which is the one at-least-once duplicate logical deduplication removes.
    /// </summary>
    [BobcatSpec]
    public async Task a_crash_after_the_commit_keeps_the_claim()
    {
        await the.ANodeStarts();
        the.CopiesOfAnIntent(2, "booking");
        var marten = the.Store is MartenStore;
        if (marten) the.MessageHoldsAfterCommit(1);

        await the.CopyIsSent(1, 1);
        if (marten) await the.MessageIsHeldAfterCommit(1);
        else await the.MessageHasCommitted(1);
        the.NodeCrashes(1);
        await the.ANodeStarts();
        await the.CopyIsSent(2, 2);
        await the.EveryCopyHasSettled();

        await the.TheDurableInvariantsHold();
        the.TheIntentsCommittedEffects("booking", 1);
        the.NoCopyWasDeadLettered();
    }

    /// <summary>
    /// A crash while the only copy is in its handler, claim taken, nothing committed. The inbox recovers the copy on
    /// the node that takes over, and it has to run: nothing else ever will. Where the claim rides the handler's
    /// transaction the crash takes it away; on the compensated path nothing releases it.
    /// </summary>
    [BobcatSpec]
    public async Task a_crash_before_the_commit_does_not_lose_the_intent()
    {
        await the.ANodeStarts();
        the.CopiesOfAnIntent(1, "rebuild");
        the.MessageMustCommit(1);
        the.CopyHoldsInItsHandler(1);

        await the.CopyIsSent(1, 1);
        await the.CopyIsHolding(1);
        await the.NodeCrashesWithACopyInFlight(1);
        await the.ANodeStarts();
        await the.EveryCopyHasSettled();

        await the.TheDurableInvariantsHold();
        the.TheIntentsCommittedEffects("rebuild", 1);
    }

    // ---------------------------------------------------------------------------------------------------------
    // 5. Collation (GH-4757)

    /// <summary>
    /// The documented behaviour, per engine and mode. CompareByString on SQL Server compares under the column's
    /// collation: the default SQL_Latin1_General_CP1_CI_AS (what the rig's server runs) folds case but not accents,
    /// and its varchar turns characters outside code page 1252 into '?'. CompareByHash, and PostgreSQL in either mode,
    /// compare the bytes.
    /// </summary>
    [BobcatSpec]
    public async Task ids_that_differ_only_by_case_accent_or_script_are_compared_as_documented()
    {
        await the.ANodeStarts();
        the.CopiesOfAnIntent(1, "Abc");
        the.CopiesOfAnIntent(1, "abc");
        the.CopiesOfAnIntent(1, "José");
        the.CopiesOfAnIntent(1, "Jose");
        the.CopiesOfAnIntent(1, "日本");
        the.CopiesOfAnIntent(1, "中国");

        await the.TheCopiesAreSent(1);
        await the.EveryCopyHasSettled();

        var collates = the.Store.Databases[0].Dialect == Dialect.SqlServer &&
                       _mode == MessageDeduplicationMode.CompareByString;

        await the.TheDurableInvariantsHold();
        the.NoCopyWasDeadLettered();
        the.TheIntentsCommittedEffects("Abc, abc", collates ? 1 : 2);
        the.TheIntentsCommittedEffects("José, Jose", 2);
        the.TheIntentsCommittedEffects("日本, 中国", collates ? 1 : 2);
    }

    // ---------------------------------------------------------------------------------------------------------
    // 6. The window

    [BobcatSpec]
    public async Task a_copy_after_the_window_and_the_reaper_runs_again()
    {
        the.ConfigureNodes(opts =>
        {
            opts.Durability.DeduplicationWindow = 2.Seconds();
            opts.Durability.DeduplicationCleanupPollingTime = 500.Milliseconds();
        });
        await the.ANodeStarts();
        the.CopiesOfAnIntent(2, "nightly");
        the.MessageMustCommit(1);
        the.MessageMustCommit(2);

        await the.CopyIsSent(1, 1);
        await the.MessageHasCommitted(1);
        await the.TheClaimHasBeenReaped("nightly");
        await the.CopyIsSent(2, 1);
        await the.EveryCopyHasSettled();

        await the.TheDurableInvariantsHold();
        the.TheIntentsCommittedEffects("nightly", 2);
    }

    /// <summary>"An expired claim is still honoured until the reaper removes it."</summary>
    [BobcatSpec]
    public async Task a_copy_after_the_window_but_before_the_reaper_is_still_refused()
    {
        the.ConfigureNodes(opts => opts.Durability.DeduplicationWindow = 1.Seconds());
        await the.ANodeStarts();
        the.CopiesOfAnIntent(2, "hourly");
        the.MessageMustCommit(1);

        await the.CopyIsSent(1, 1);
        await the.MessageHasCommitted(1);
        await the.TimePasses(3000);
        await the.CopyIsSent(2, 1);
        await the.EveryCopyHasSettled();

        await the.TheDurableInvariantsHold();
        the.TheIntentsCommittedEffects("hourly", 1);
    }
}
