using CoreTests.Runtime;
using JasperFx;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Persistence;

/// <summary>
/// F19 from the durable compliance rig. <see cref="DurableReceiver" /> only builds its InboxStaleTime heartbeat
/// when its inbox is an <see cref="IMessageInboxHeartbeat" />, and <see cref="MultiTenantedMessageStore" /> was not
/// one. So behind a database-per-tenant store NO row got a heartbeat -- tenanted or not -- while every tenant
/// database still ran the stale sweep. A handler that outlived InboxStaleTime had its row released underneath it,
/// and another node ran it alongside. <c>ServerlessWithOutbox</c> sets InboxStaleTime by default.
/// </summary>
public class multi_tenanted_store_restarts_each_tenants_stale_clock
{
    private readonly IMessageStore theMainStore = Substitute.For<IMessageStore>();
    private readonly IMessageStore theTenantStore = Substitute.For<IMessageStore>();
    private readonly IMessageInbox theMainInbox = Substitute.For<IMessageInbox, IMessageInboxHeartbeat>();
    private readonly IMessageInbox theTenantInbox = Substitute.For<IMessageInbox, IMessageInboxHeartbeat>();
    private readonly ITenantedMessageSource theSource = Substitute.For<ITenantedMessageSource>();
    private readonly MockWolverineRuntime theRuntime = new();
    private readonly MultiTenantedMessageStore theStore;

    public multi_tenanted_store_restarts_each_tenants_stale_clock()
    {
        theRuntime.Options.AutoBuildMessageStorageOnStartup = AutoCreate.None;

        theMainStore.Inbox.Returns(theMainInbox);
        theTenantStore.Inbox.Returns(theTenantInbox);
        theSource.FindAsync("tenant1").Returns(theTenantStore);

        theStore = new MultiTenantedMessageStore(theMainStore, theRuntime, theSource);
    }

    private static Envelope anEnvelopeFor(string? tenantId)
    {
        var envelope = ObjectMother.Envelope();
        envelope.TenantId = tenantId;
        return envelope;
    }

    [Fact]
    public async Task each_envelope_s_clock_is_restarted_in_the_database_that_holds_its_row()
    {
        var heartbeat = theStore as IMessageInboxHeartbeat;
        heartbeat.ShouldNotBeNull("A durable receiver behind a database-per-tenant store gets no heartbeat at all");

        var untenanted = anEnvelopeFor(null);
        var tenanted = anEnvelopeFor("tenant1");

        await heartbeat.RestartStaleClockAsync(3, [untenanted, tenanted]);

        await ((IMessageInboxHeartbeat)theMainInbox).Received()
            .RestartStaleClockAsync(3, Arg.Is<IReadOnlyList<Envelope>>(x => x.Count == 1 && x[0] == untenanted));
        await ((IMessageInboxHeartbeat)theTenantInbox).Received()
            .RestartStaleClockAsync(3, Arg.Is<IReadOnlyList<Envelope>>(x => x.Count == 1 && x[0] == tenanted));
    }

    [Fact]
    public async Task a_tenant_database_that_refuses_does_not_stop_the_others()
    {
        var heartbeat = (IMessageInboxHeartbeat)theStore;
        ((IMessageInboxHeartbeat)theTenantInbox).RestartStaleClockAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<Envelope>>())
            .ThrowsAsync(new TimeoutException("tenant1 is down"));

        var untenanted = anEnvelopeFor(null);

        await Should.ThrowAsync<TimeoutException>(() =>
            heartbeat.RestartStaleClockAsync(3, [anEnvelopeFor("tenant1"), untenanted]));

        await ((IMessageInboxHeartbeat)theMainInbox).Received()
            .RestartStaleClockAsync(3, Arg.Is<IReadOnlyList<Envelope>>(x => x.Count == 1 && x[0] == untenanted));
    }

    [Fact]
    public void a_durable_receiver_behind_the_composite_store_builds_its_heartbeat()
    {
        var runtime = new MockWolverineRuntime();
        runtime.Options.AutoBuildMessageStorageOnStartup = AutoCreate.None;
        runtime.DurabilitySettings.InboxStaleTime = TimeSpan.FromSeconds(30);
        runtime.Storage.Inbox.Returns(theStore);

        var receiver = new DurableReceiver(new StubEndpoint("f19", new StubTransport()), runtime,
            Substitute.For<IHandlerPipeline>());

        try
        {
            receiver.HasInboxHeartbeat.ShouldBeTrue();
        }
        finally
        {
            receiver.Dispose();
        }
    }
}
