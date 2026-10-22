using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using PostgresqlTests.MultiTenancy;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace PostgresqlTests.Bugs;

/// <summary>
/// GH-5123. After a broker outage, the broker redelivers a publish it buffered AND the outbox resends it because the
/// confirm never came back, so two copies with the same envelope id land in ONE micro-batch at a durable listener.
/// Neither copy is in the inbox yet. The batched insert used to rethrow the raw unique violation, which the receiver
/// read as "the inbox database is down" and paused the listener over an ordinary duplicate.
/// </summary>
public class Bug_5123_intra_batch_duplicate_at_the_durable_receiver : IAsyncLifetime
{
    private IHost theHost = null!;
    private DurableReceiver theReceiver = null!;
    private readonly IListener theListener = Substitute.For<IListener>();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "gh5123_receiver");
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await theHost.ResetResourceState();

        var runtime = theHost.GetRuntime();
        theReceiver = new DurableReceiver(new StubEndpoint("gh5123", new StubTransport()), runtime, thePipeline);
    }

    public async ValueTask DisposeAsync()
    {
        await theReceiver.DisposeAsync();
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task two_copies_in_one_batch_do_not_pause_the_listener_and_run_once()
    {
        var original = ObjectMother.Envelope();
        var copy = ObjectMother.Envelope();
        copy.Id = original.Id;
        copy.MessageType = original.MessageType;

        await theReceiver.ProcessReceivedMessagesAsync(DateTimeOffset.UtcNow, theListener, [original, copy]);
        await theReceiver.DrainAsync();

        theReceiver.InboxUnavailableSignaled.ShouldBeFalse(
            "a duplicate inside one batch is ordinary at-least-once traffic, not an unavailable inbox");

        // Both deliveries are settled with the broker, one as the stored message and one as its duplicate...
        await theListener.Received().CompleteAsync(original);
        await theListener.Received().CompleteAsync(copy);
        await theListener.DidNotReceive().DeferAsync(Arg.Any<Envelope>());

        // ...and exactly one of them reaches the handlers
        thePipeline.ReceivedCalls().Count(x => x.GetMethodInfo().Name == nameof(IHandlerPipeline.InvokeAsync))
            .ShouldBe(1);
    }
}

/// <summary>
/// GH-5123. With one message database per tenant, the same intra-batch duplicate reached
/// <see cref="MultiTenantedMessageStore" /> as a raw provider exception, which it counts as the tenant's database
/// failing: it tripped the tenant write brake, both copies were deferred back to the broker, came back together, and
/// were refused again -- 2,748 publishes of one message on one queue in the reporter's run.
/// </summary>
public class Bug_5123_intra_batch_duplicate_in_a_tenant_database : MultiTenancyContext
{
    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "gh5123_tenants")
            .RegisterStaticTenants(tenants => tenants.Register("red", tenant1ConnectionString));

        opts.Services.AddResourceSetupOnStartup();
    }

    private static Envelope incoming(string tenantId)
    {
        var envelope = ObjectMother.Envelope();
        envelope.TenantId = tenantId;
        envelope.Destination = new Uri("stub://gh5123-tenants");
        envelope.Status = EnvelopeStatus.Incoming;
        return envelope;
    }

    [Fact]
    public async Task an_intra_batch_duplicate_is_a_duplicate_and_does_not_trip_the_tenant_write_brake()
    {
        var store = theHost.Services.GetRequiredService<IMessageStore>().ShouldBeOfType<MultiTenantedMessageStore>();
        IMessageInbox inbox = store;

        var original = incoming("red");
        var copy = incoming("red");
        copy.Id = original.Id;

        await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(() => inbox.StoreIncomingAsync([original, copy]));

        // A tripped brake refuses the tenant's next write for a whole cycle without touching the database
        var next = incoming("red");
        await inbox.StoreIncomingAsync([next, incoming("red")]);

        (await inbox.ExistsAsync(next, CancellationToken.None)).ShouldBeTrue();
    }
}
