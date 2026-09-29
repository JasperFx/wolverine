using CoreTests.Runtime;
using JasperFx;
using JasperFx.MultiTenancy;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Xunit;

namespace CoreTests.Persistence.Durability;

/// <summary>
/// GH-4658, the hole left by GH-4435. StoreIncomingAsync(IReadOnlyList) has a fast path for a batch that
/// resolves to a single store, and that path let everything the store threw reach the caller RAW. A raw
/// exception misses DurableReceiver's <see cref="TenantedInboxWriteException.IncludesMainStore" /> check,
/// so one tenant's database being down paused the listener for every other tenant -- the exact thing the
/// rest of GH-4435 exists to prevent.
///
/// <para>
/// With a broker in front, a single-store batch is the COMMON case rather than an edge: a stranded
/// tenant's message usually arrives on its own, and so does every redelivery of a deferred one. Which
/// meant whether a tenant outage paused the listener came down to batch composition.
/// </para>
/// </summary>
public class single_store_inbox_batches_4658
{
    private readonly IMessageStore theMainStore = Substitute.For<IMessageStore>();
    private readonly IMessageStore theRedStore = Substitute.For<IMessageStore>();
    private readonly ITenantedMessageSource theSource = Substitute.For<ITenantedMessageSource>();
    private readonly MultiTenantedMessageStore theStore;

    public single_store_inbox_batches_4658()
    {
        var runtime = new MockWolverineRuntime();

        // Keep GetDatabaseAsync from trying to migrate a substitute on first resolution
        runtime.Options.AutoBuildMessageStorageOnStartup = AutoCreate.None;

        theSource.FindAsync("red").Returns(theRedStore);

        theStore = new MultiTenantedMessageStore(theMainStore, runtime, theSource);
    }

    private static Envelope envelopeFor(string? tenantId)
    {
        var envelope = ObjectMother.Envelope();
        envelope.TenantId = tenantId!;
        return envelope;
    }

    [Fact]
    public async Task a_batch_that_belongs_entirely_to_one_tenant_is_a_tenant_scoped_failure()
    {
        theRedStore.Inbox.StoreIncomingAsync(Arg.Any<IReadOnlyList<Envelope>>())
            .Throws(new TimeoutException("tenant database is unreachable"));

        var first = envelopeFor("red");
        var second = envelopeFor("red");

        // This used to throw the raw TimeoutException, which DurableReceiver reads as "the inbox is
        // unavailable" and answers by pausing the listener for EVERY tenant.
        var ex = await Should.ThrowAsync<TenantedInboxWriteException>(
            () => theStore.Inbox.StoreIncomingAsync([first, second]));

        ex.IncludesMainStore.ShouldBeFalse();
        ex.Unpersisted.ShouldBe([first, second]);

        // Nothing landed, so nothing may be stamped -- the receiver's per-envelope fallback has to
        // re-attempt both of these, and a stamped envelope is acked without ever being stored.
        first.WasPersistedInInbox.ShouldBeFalse();
        second.WasPersistedInInbox.ShouldBeFalse();
    }

    [Fact]
    public async Task many_tenants_on_one_tenant_database_are_also_tenant_scoped()
    {
        // Two tenant ids, one database behind them. GH-4435 made grouping resolve to the STORE, which is
        // what lands a batch like this on the single-group path in the first place.
        theSource.FindAsync("blue").Returns(theRedStore);

        theRedStore.Inbox.StoreIncomingAsync(Arg.Any<IReadOnlyList<Envelope>>())
            .Throws(new TimeoutException("tenant database is unreachable"));

        var ex = await Should.ThrowAsync<TenantedInboxWriteException>(
            () => theStore.Inbox.StoreIncomingAsync([envelopeFor("red"), envelopeFor("blue")]));

        ex.IncludesMainStore.ShouldBeFalse();
        ex.Unpersisted.Count.ShouldBe(2);
    }

    /// <summary>
    /// A REGRESSION PIN, not evidence: this passed before GH-4658 too, because the single-group path
    /// already let everything through untouched. It is here so the new wrapping cannot swallow the one
    /// exception type DurableReceiver's deduplication path keys off.
    /// </summary>
    [Fact]
    public async Task a_duplicate_from_the_one_store_still_surfaces_as_a_duplicate()
    {
        var red = envelopeFor("red");

        theRedStore.Inbox.StoreIncomingAsync(Arg.Any<IReadOnlyList<Envelope>>())
            .Throws(new DuplicateIncomingEnvelopeException([red]));

        var ex = await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(
            () => theStore.Inbox.StoreIncomingAsync([red]));

        ex.Duplicates.ShouldHaveSingleItem().ShouldBeSameAs(red);
    }

    /// <summary>
    /// The other REGRESSION PIN, and the reason the fix is not simply "wrap everything". A batch that
    /// belongs entirely to the MAIN store must still propagate raw: nothing can be persisted at all in
    /// that case, and pausing for inbox recovery is the correct answer. Also passed before GH-4658.
    /// </summary>
    [Fact]
    public async Task a_batch_that_belongs_entirely_to_the_main_store_still_propagates_raw()
    {
        theMainStore.Inbox.StoreIncomingAsync(Arg.Any<IReadOnlyList<Envelope>>())
            .Throws(new TimeoutException("the main database is unreachable"));

        // A null tenant id resolves to the main store
        await Should.ThrowAsync<TimeoutException>(
            () => theStore.Inbox.StoreIncomingAsync([envelopeFor(null), envelopeFor(null)]));
    }
}
