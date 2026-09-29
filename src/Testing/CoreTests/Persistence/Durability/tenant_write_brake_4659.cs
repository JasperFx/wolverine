using CoreTests.Runtime;
using JasperFx;
using JasperFx.Core;
using JasperFx.MultiTenancy;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Xunit;

namespace CoreTests.Persistence.Durability;

/// <summary>
/// GH-4659. Since the tenant-scoped defer landed (GH-4435), a stranded envelope whose tenant database is
/// down is deferred back to the broker and handed straight back — on every transport, with nothing in
/// between. So it goes round as fast as the database can refuse a connection.
///
/// <para>
/// Measured by the reporter: ~300 turns a second from a <b>single</b> stranded message on PostgreSQL,
/// 620/s on SQL Server, and a hundred stranded messages taking a Windows host to ~7,000 of its ~15,700
/// ephemeral ports in TIME_WAIT — ports shared with the broker, the telemetry, and every healthy tenant's
/// connections.
/// </para>
///
/// <para>
/// The brake makes the refusal free rather than slowing the redelivery down. Slowing it down is not
/// available here: the tenant-scoped defer runs inline on the listener's own single-worker dispatch thread,
/// so waiting there would stall every other tenant on the endpoint — trading the cross-tenant pause GH-4658
/// removed for a cross-tenant stall.
/// </para>
/// </summary>
public class tenant_write_brake_4659
{
    private readonly IMessageStore theMain = Substitute.For<IMessageStore>();
    private readonly IMessageStore theTenantStore = Substitute.For<IMessageStore>();
    private readonly ITenantedMessageSource theSource = Substitute.For<ITenantedMessageSource>();

    private MultiTenantedMessageStore theStore = null!;

    private IMessageInbox theTenantInbox => theTenantStore.Inbox;

    private MultiTenantedMessageStore storeWith(TimeSpan cycle)
    {
        var runtime = new MockWolverineRuntime();
        runtime.Options.AutoBuildMessageStorageOnStartup = AutoCreate.None;
        runtime.DurabilitySettings.TenantWriteBrakeCycle = cycle;

        theSource.FindAsync("red").Returns(theTenantStore);

        return new MultiTenantedMessageStore(theMain, runtime, theSource);
    }

    private static Envelope envelopeFor(string tenantId)
    {
        var envelope = ObjectMother.Envelope();
        envelope.TenantId = tenantId;
        return envelope;
    }

    private void theTenantDatabaseIsDown()
    {
        theTenantInbox.StoreIncomingAsync(Arg.Any<Envelope>())
            .Returns(_ => Task.FromException(new TimeoutException("the tenant database is unreachable")));
    }

    [Fact]
    public async Task a_refused_tenant_write_holds_the_next_ones_without_connecting()
    {
        theTenantDatabaseIsDown();
        theStore = storeWith(10.Seconds());

        // The spin: the same envelope comes back from the broker over and over while the database is down.
        for (var i = 0; i < 50; i++)
        {
            await Should.ThrowAsync<TenantedInboxWriteException>(() =>
                ((IMessageInbox)theStore).StoreIncomingAsync(envelopeFor("red")));
        }

        // Before the brake this was 50 connection attempts. Now it is the one that tripped it: the cycle
        // is longer than this loop takes, so no probe is due.
        await theTenantInbox.Received(1).StoreIncomingAsync(Arg.Any<Envelope>());
    }

    [Fact]
    public async Task the_held_failure_names_the_tenant_and_stays_tenant_scoped()
    {
        theTenantDatabaseIsDown();
        theStore = storeWith(10.Seconds());

        await Should.ThrowAsync<TenantedInboxWriteException>(() =>
            ((IMessageInbox)theStore).StoreIncomingAsync(envelopeFor("red")));

        var held = await Should.ThrowAsync<TenantedInboxWriteException>(() =>
            ((IMessageInbox)theStore).StoreIncomingAsync(envelopeFor("red")));

        // Still tenant-scoped, so the listener keeps running for everyone else. That is the whole point:
        // the brake must not look like a main-store failure.
        held.IncludesMainStore.ShouldBeFalse();
        held.TenantIds.ShouldContain("red");
        held.InnerException.ShouldBeOfType<TenantDatabaseBrakedException>()
            .TenantId.ShouldBe("red");
    }

    [Fact]
    public async Task one_probe_per_cycle_is_let_through()
    {
        theTenantDatabaseIsDown();
        theStore = storeWith(150.Milliseconds());

        for (var i = 0; i < 20; i++)
        {
            await Should.ThrowAsync<TenantedInboxWriteException>(() =>
                ((IMessageInbox)theStore).StoreIncomingAsync(envelopeFor("red")));
        }

        await Task.Delay(250.Milliseconds(), TestContext.Current.CancellationToken);

        for (var i = 0; i < 20; i++)
        {
            await Should.ThrowAsync<TenantedInboxWriteException>(() =>
                ((IMessageInbox)theStore).StoreIncomingAsync(envelopeFor("red")));
        }

        // One for the trip, one for the probe after the cycle elapsed -- not 40.
        await theTenantInbox.Received(2).StoreIncomingAsync(Arg.Any<Envelope>());
    }

    [Fact]
    public async Task a_recovered_database_releases_the_brake_immediately()
    {
        var attempts = 0;
        theTenantInbox.StoreIncomingAsync(Arg.Any<Envelope>()).Returns(_ =>
        {
            attempts++;
            return attempts == 1
                ? Task.FromException(new TimeoutException("down"))
                : Task.CompletedTask;
        });

        theStore = storeWith(100.Milliseconds());

        await Should.ThrowAsync<TenantedInboxWriteException>(() =>
            ((IMessageInbox)theStore).StoreIncomingAsync(envelopeFor("red")));

        await Task.Delay(150.Milliseconds(), TestContext.Current.CancellationToken);

        // The probe succeeds, and from then on nothing is held back at all.
        await ((IMessageInbox)theStore).StoreIncomingAsync(envelopeFor("red"));
        await ((IMessageInbox)theStore).StoreIncomingAsync(envelopeFor("red"));
        await ((IMessageInbox)theStore).StoreIncomingAsync(envelopeFor("red"));

        attempts.ShouldBe(4);
    }

    [Fact]
    public async Task the_main_store_is_never_braked()
    {
        // A main-store failure pauses the listener for inbox recovery, which is its own back-off. Holding
        // it back would delay the recovery probe itself.
        theMain.Inbox.StoreIncomingAsync(Arg.Any<Envelope>())
            .Returns(_ => Task.FromException(new TimeoutException("main is unreachable")));

        theStore = storeWith(10.Seconds());

        for (var i = 0; i < 5; i++)
        {
            await Should.ThrowAsync<TimeoutException>(() =>
                ((IMessageInbox)theStore).StoreIncomingAsync(envelopeFor(null!)));
        }

        await theMain.Inbox.Received(5).StoreIncomingAsync(Arg.Any<Envelope>());
    }
}
