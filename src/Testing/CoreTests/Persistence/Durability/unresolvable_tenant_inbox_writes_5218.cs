using CoreTests.Runtime;
using CoreTests.Runtime.WorkerQueues;
using JasperFx;
using JasperFx.Core;
using JasperFx.MultiTenancy;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Persistence.Durability;

/// <summary>
/// GH-5218. A tenant whose message store cannot be RESOLVED (the tenant source throws from FindAsync) has
/// to be treated like a tenant whose database is down: its envelope is deferred back to the broker and
/// the listener keeps running for every other tenant. The batch overload of StoreIncomingAsync has done
/// that since GH-4435, but the single-envelope overload resolved the tenant outside its tenant-scoped
/// handling, so the raw resolution failure paused the whole listener.
/// </summary>
public class unresolvable_tenant_inbox_writes_5218
{
    private readonly IMessageStore theMain = Substitute.For<IMessageStore>();
    private readonly ITenantedMessageSource theSource = Substitute.For<ITenantedMessageSource>();
    private readonly InvalidOperationException theResolutionFailure = new("tenant-a cannot be resolved");
    private IMessageStore? theResolvedTenantA;

    private IMessageInbox storeWith(TimeSpan? brakeCycle = null)
    {
        var runtime = new MockWolverineRuntime();
        runtime.Options.AutoBuildMessageStorageOnStartup = AutoCreate.None;
        runtime.DurabilitySettings.TenantWriteBrakeCycle = brakeCycle ?? 10.Seconds();

        theSource.FindAsync("tenant-a").Returns<ValueTask<IMessageStore>>(_ =>
            new ValueTask<IMessageStore>(theResolvedTenantA ?? throw theResolutionFailure));

        return new MultiTenantedMessageStore(theMain, runtime, theSource);
    }

    private static Envelope envelopeFor(string? tenantId)
    {
        var envelope = ObjectMother.Envelope();
        envelope.TenantId = tenantId;
        return envelope;
    }

    [Fact]
    public async Task single_envelope_write_for_an_unresolvable_tenant_is_tenant_scoped()
    {
        var inbox = storeWith();
        var envelope = envelopeFor("tenant-a");

        var ex = await Should.ThrowAsync<TenantedInboxWriteException>(() => inbox.StoreIncomingAsync(envelope));

        ex.IncludesMainStore.ShouldBeFalse();
        ex.Unpersisted.ShouldBe([envelope]);
        ex.TenantIds.ShouldBe(["tenant-a"]);
        ex.InnerException.ShouldBeSameAs(theResolutionFailure);
    }

    [Fact]
    public async Task batch_write_for_an_unresolvable_tenant_is_tenant_scoped()
    {
        // Already true since GH-4435 -- here so the two overloads stay in step
        var inbox = storeWith();

        var ex = await Should.ThrowAsync<TenantedInboxWriteException>(() =>
            inbox.StoreIncomingAsync(new List<Envelope> { envelopeFor("tenant-a") }));

        ex.IncludesMainStore.ShouldBeFalse();
        ex.TenantIds.ShouldBe(["tenant-a"]);
    }

    [Fact]
    public async Task an_unresolvable_tenant_is_not_resolved_again_on_every_redelivery()
    {
        // The GH-4659 brake is keyed on the resolved store, so it could never engage here. A deferred
        // envelope comes straight back from the broker, and every turn used to call the tenant source again.
        var inbox = storeWith(10.Seconds());

        for (var i = 0; i < 25; i++)
        {
            await Should.ThrowAsync<TenantedInboxWriteException>(() =>
                inbox.StoreIncomingAsync(envelopeFor("tenant-a")));

            await Should.ThrowAsync<TenantedInboxWriteException>(() =>
                inbox.StoreIncomingAsync(new List<Envelope> { envelopeFor("tenant-a") }));
        }

        await theSource.Received(1).FindAsync("tenant-a");
    }

    [Fact]
    public async Task a_held_resolution_stays_tenant_scoped_and_names_the_tenant()
    {
        var inbox = storeWith(10.Seconds());

        await Should.ThrowAsync<TenantedInboxWriteException>(() => inbox.StoreIncomingAsync(envelopeFor("tenant-a")));

        var held = await Should.ThrowAsync<TenantedInboxWriteException>(() =>
            inbox.StoreIncomingAsync(envelopeFor("tenant-a")));

        held.IncludesMainStore.ShouldBeFalse();
        held.InnerException.ShouldBeOfType<TenantDatabaseBrakedException>().TenantId.ShouldBe("tenant-a");
    }

    [Fact]
    public async Task a_tenant_that_becomes_resolvable_is_written_after_the_cool_down()
    {
        var inbox = storeWith(100.Milliseconds());
        await Should.ThrowAsync<TenantedInboxWriteException>(() => inbox.StoreIncomingAsync(envelopeFor("tenant-a")));

        var tenantStore = Substitute.For<IMessageStore>();
        theResolvedTenantA = tenantStore;

        await Task.Delay(150.Milliseconds(), TestContext.Current.CancellationToken);

        var envelope = envelopeFor("tenant-a");
        await inbox.StoreIncomingAsync(envelope);

        await tenantStore.Inbox.Received(1).StoreIncomingAsync(envelope);
    }

    [Fact]
    public async Task one_unresolvable_tenant_does_not_hold_back_another()
    {
        var inbox = storeWith(10.Seconds());
        var tenantB = Substitute.For<IMessageStore>();
        theSource.FindAsync("tenant-b").Returns(tenantB);

        await Should.ThrowAsync<TenantedInboxWriteException>(() => inbox.StoreIncomingAsync(envelopeFor("tenant-a")));

        var envelope = envelopeFor("tenant-b");
        await inbox.StoreIncomingAsync(envelope);

        await tenantB.Inbox.Received(1).StoreIncomingAsync(envelope);
    }

    [Fact]
    public async Task the_main_store_failure_still_propagates_raw()
    {
        var inbox = storeWith();
        theMain.Inbox.StoreIncomingAsync(Arg.Any<Envelope>())
            .Returns(_ => Task.FromException(new TimeoutException("main is unreachable")));

        await Should.ThrowAsync<TimeoutException>(() => inbox.StoreIncomingAsync(envelopeFor(null)));
    }
}

/// <summary>
/// GH-5218, end to end through <see cref="DurableReceiver" /> with a real
/// <see cref="MultiTenantedMessageStore" /> behind it.
/// </summary>
public class durable_receiver_with_an_unresolvable_tenant_5218
{
    private readonly IMessageStore theMain = Substitute.For<IMessageStore>();
    private readonly ITenantedMessageSource theSource = Substitute.For<ITenantedMessageSource>();
    private readonly IListener theListener = Substitute.For<IListener>();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly CapturedLogs theLogs = new();
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly DurableReceiver theReceiver;

    public durable_receiver_with_an_unresolvable_tenant_5218()
    {
        theRuntime.LoggerFactory.AddProvider(theLogs);
        theRuntime.Options.AutoBuildMessageStorageOnStartup = AutoCreate.None;

        theSource.FindAsync("tenant-a").Returns<ValueTask<IMessageStore>>(_ =>
            throw new InvalidOperationException("tenant-a cannot be resolved"));

        IMessageInbox multiTenanted = new MultiTenantedMessageStore(theMain, theRuntime, theSource);
        theRuntime.Storage.Inbox.Returns(multiTenanted);

        theReceiver = new DurableReceiver(new StubEndpoint("one", new StubTransport()), theRuntime, thePipeline);
    }

    private static Envelope envelopeFor(string? tenantId)
    {
        var envelope = ObjectMother.Envelope();
        envelope.TenantId = tenantId;
        return envelope;
    }

    [Fact]
    public async Task a_single_envelope_for_an_unresolvable_tenant_does_not_pause_the_listener()
    {
        var envelope = envelopeFor("tenant-a");

        await theReceiver.ReceivedAsync(theListener, envelope);
        await theReceiver.DrainAsync();

        theReceiver.InboxUnavailableSignaled.ShouldBeFalse();
        await theListener.Received().DeferAsync(envelope);
        await theListener.DidNotReceive().CompleteAsync(envelope);
    }

    [Fact]
    public async Task a_batch_for_an_unresolvable_tenant_does_not_pause_the_listener()
    {
        // The batch failure is tenant-scoped, but the per-envelope fallback re-attempts each envelope
        // through the single-envelope write -- which is where the pause came from.
        var envelope = envelopeFor("tenant-a");

        await theReceiver.ProcessReceivedMessagesAsync(DateTimeOffset.UtcNow, theListener, [envelope]);
        await theReceiver.DrainAsync();

        theReceiver.InboxUnavailableSignaled.ShouldBeFalse();
        await theListener.Received().DeferAsync(envelope);
    }

    [Fact]
    public async Task the_deferral_warning_names_the_tenant()
    {
        await theReceiver.ReceivedAsync(theListener, envelopeFor("tenant-a"));
        await theReceiver.DrainAsync();

        theLogs.Warnings.ShouldContain(x => x.Contains("Inbox write failed for") && x.Contains("tenant-a"));
    }

    [Fact]
    public async Task the_pause_warning_carries_the_exception_that_caused_it()
    {
        var failure = new TimeoutException("main is unreachable");
        theMain.Inbox.StoreIncomingAsync(Arg.Any<Envelope>()).Returns(_ => Task.FromException(failure));

        await theReceiver.ReceivedAsync(theListener, envelopeFor(null));
        await theReceiver.DrainAsync();

        theReceiver.InboxUnavailableSignaled.ShouldBeTrue();
        theLogs.WarningExceptions.ShouldContain(x =>
            x.Message.Contains("Signaling listener to pause") && ReferenceEquals(x.Exception, failure));
    }
}
