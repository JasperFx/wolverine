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
/// GH-4435. A durable listener hands MultiTenantedMessageStore batches that span tenants. Before this,
/// a multi-group batch was posted to a RetryBlock, which never rethrows -- so a tenant database that
/// could not take its half of the batch produced a clean return, the receiver acked everything, and
/// envelopes that were never stored went to the handler with no inbox row anywhere.
/// </summary>
public class mixed_tenant_inbox_batches_4435
{
    private readonly IMessageStore theMainStore = Substitute.For<IMessageStore>();
    private readonly IMessageStore theRedStore = Substitute.For<IMessageStore>();
    private readonly IMessageStore theBlueStore = Substitute.For<IMessageStore>();
    private readonly ITenantedMessageSource theSource = Substitute.For<ITenantedMessageSource>();
    private readonly MultiTenantedMessageStore theStore;

    public mixed_tenant_inbox_batches_4435()
    {
        var runtime = new MockWolverineRuntime();

        // Keep GetDatabaseAsync from trying to migrate a substitute on first resolution
        runtime.Options.AutoBuildMessageStorageOnStartup = AutoCreate.None;

        theSource.FindAsync("red").Returns(theRedStore);
        theSource.FindAsync("blue").Returns(theBlueStore);

        theStore = new MultiTenantedMessageStore(theMainStore, runtime, theSource);
    }

    private static Envelope envelopeFor(string? tenantId)
    {
        var envelope = ObjectMother.Envelope();
        envelope.TenantId = tenantId!;
        return envelope;
    }

    [Fact]
    public async Task many_tenants_on_one_database_are_stored_as_a_single_batch()
    {
        // Two distinct tenant ids, one database behind them. Grouping by tenant id sent this down the
        // multi-group path; grouping by the RESOLVED store keeps it on the single awaited round trip.
        theSource.FindAsync("blue").Returns(theRedStore);

        var envelopes = new List<Envelope> { envelopeFor("red"), envelopeFor("blue"), envelopeFor("red") };

        await theStore.Inbox.StoreIncomingAsync(envelopes);

        await theRedStore.Inbox.Received(1).StoreIncomingAsync(
            Arg.Is<IReadOnlyList<Envelope>>(x => x.Count == 3));
    }

    [Fact]
    public async Task a_tenant_database_that_cannot_take_its_half_of_the_batch_throws()
    {
        theRedStore.Inbox.StoreIncomingAsync(Arg.Any<IReadOnlyList<Envelope>>())
            .Throws(new TimeoutException("tenant database is unreachable"));

        var envelopes = new List<Envelope> { envelopeFor("red"), envelopeFor("blue") };

        // Used to return cleanly, which is the whole bug
        await Should.ThrowAsync<TenantedInboxWriteException>(
            () => theStore.Inbox.StoreIncomingAsync(envelopes));
    }

    [Fact]
    public async Task the_exception_names_only_the_envelopes_that_never_landed()
    {
        var red = envelopeFor("red");
        var blue = envelopeFor("blue");

        theRedStore.Inbox.StoreIncomingAsync(Arg.Any<IReadOnlyList<Envelope>>())
            .Throws(new TimeoutException("tenant database is unreachable"));

        var ex = await Should.ThrowAsync<TenantedInboxWriteException>(
            () => theStore.Inbox.StoreIncomingAsync([red, blue]));

        ex.Unpersisted.ShouldHaveSingleItem().ShouldBeSameAs(red);
    }

    [Fact]
    public async Task envelopes_whose_own_store_committed_are_marked_as_persisted()
    {
        var red = envelopeFor("red");
        var blue = envelopeFor("blue");

        theRedStore.Inbox.StoreIncomingAsync(Arg.Any<IReadOnlyList<Envelope>>())
            .Throws(new TimeoutException("tenant database is unreachable"));

        await Should.ThrowAsync<TenantedInboxWriteException>(
            () => theStore.Inbox.StoreIncomingAsync([red, blue]));

        // The receiver re-runs the whole batch one envelope at a time. Without this flag the committed
        // envelope is stored again, reads as a duplicate, and is settled WITHOUT ever being handled.
        blue.WasPersistedInInbox.ShouldBeTrue();
        red.WasPersistedInInbox.ShouldBeFalse();
    }

    [Fact]
    public async Task a_tenant_only_outage_is_not_reported_as_a_main_store_failure()
    {
        theRedStore.Inbox.StoreIncomingAsync(Arg.Any<IReadOnlyList<Envelope>>())
            .Throws(new TimeoutException("tenant database is unreachable"));

        var ex = await Should.ThrowAsync<TenantedInboxWriteException>(
            () => theStore.Inbox.StoreIncomingAsync([envelopeFor("red"), envelopeFor("blue")]));

        // This is what keeps DurableReceiver from pausing a listener that serves every other tenant
        ex.IncludesMainStore.ShouldBeFalse();
    }

    [Fact]
    public async Task a_main_store_failure_is_reported_as_such()
    {
        theMainStore.Inbox.StoreIncomingAsync(Arg.Any<IReadOnlyList<Envelope>>())
            .Throws(new TimeoutException("the main database is unreachable"));

        // A null tenant id resolves to the main store
        var ex = await Should.ThrowAsync<TenantedInboxWriteException>(
            () => theStore.Inbox.StoreIncomingAsync([envelopeFor(null), envelopeFor("red")]));

        ex.IncludesMainStore.ShouldBeTrue();
    }

    [Fact]
    public async Task a_duplicate_inside_a_mixed_batch_still_surfaces_as_a_duplicate()
    {
        var red = envelopeFor("red");

        theRedStore.Inbox.StoreIncomingAsync(Arg.Any<IReadOnlyList<Envelope>>())
            .Throws(new DuplicateIncomingEnvelopeException([red]));

        // Defect 2: the RetryBlock swallowed this, the batch counted as stored, and the redelivered
        // copy was handled a second time. DurableReceiver's dedup path keys off this exact type.
        var ex = await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(
            () => theStore.Inbox.StoreIncomingAsync([red, envelopeFor("blue")]));

        ex.Duplicates.ShouldHaveSingleItem().ShouldBeSameAs(red);
    }

    [Fact]
    public async Task an_unresolvable_tenant_is_reported_rather_than_silently_dropped()
    {
        // UnknownTenantIdException specifically: it is the ONE exception the old code caught and skipped,
        // so it is the only one that distinguishes this fix from the behaviour that was already there.
        theSource.FindAsync("ghost").Throws(new UnknownTenantIdException("ghost"));

        var ghost = envelopeFor("ghost");
        var blue = envelopeFor("blue");

        // This used to be logged and skipped: the envelopes were never stored, the caller read the clean
        // return as success and acked them, and they were simply gone.
        var ex = await Should.ThrowAsync<TenantedInboxWriteException>(
            () => theStore.Inbox.StoreIncomingAsync([ghost, blue]));

        ex.Unpersisted.ShouldHaveSingleItem().ShouldBeSameAs(ghost);

        // A tenant that cannot be resolved is still not a reason to pause the whole listener
        ex.IncludesMainStore.ShouldBeFalse();

        // ...and the tenant that resolved fine still committed
        blue.WasPersistedInInbox.ShouldBeTrue();
    }

    /// <summary>
    /// The real-world shape: a StaticTenantSource throws ArgumentOutOfRangeException for an unregistered
    /// tenant, NOT UnknownTenantIdException -- which is why the old typed catch never covered the common
    /// case even when it was reached. Either way the envelopes must be reported, never dropped.
    /// </summary>
    [Fact]
    public async Task a_static_tenant_source_miss_is_also_reported()
    {
        theSource.FindAsync("ghost").Throws(new ArgumentOutOfRangeException("tenantId", "Unknown tenant id"));

        var ex = await Should.ThrowAsync<TenantedInboxWriteException>(
            () => theStore.Inbox.StoreIncomingAsync([envelopeFor("ghost"), envelopeFor("blue")]));

        ex.Unpersisted.ShouldHaveSingleItem();
    }
}
