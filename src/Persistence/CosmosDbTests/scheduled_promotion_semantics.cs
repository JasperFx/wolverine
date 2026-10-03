using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.CosmosDb;
using Wolverine.CosmosDb.Internals;
using Wolverine.Persistence.Durability;
using Wolverine.Transports;
using Wolverine.Transports.Tcp;
using Wolverine.Util;

namespace CosmosDbTests;

public class PromotedMessageCatcher
{
    public readonly TaskCompletionSource<Envelope> Source = new();
}

public record PromotedMessage(int Id);

public class PromotedMessageHandler
{
    public static void Handle(PromotedMessage message, Envelope envelope, PromotedMessageCatcher catcher)
    {
        catcher.Source.TrySetResult(envelope);
    }
}

/// <summary>
/// GH-4216 (1b). CosmosDb is not an <see cref="IMessageDatabase" />, so every scheduled-promotion contract in
/// <c>MessageStoreCompliance</c> early-returns here and the semantics those contracts pin have never been
/// asserted for this store. The skip is legitimate -- CosmosDb promotes from its own durability agent rather
/// than through <c>PollForScheduledMessagesAsync</c> -- but the questions still have answers, and this asks
/// them in the shape CosmosDb actually has rather than porting the relational tests.
///
/// One of GH-4216's three questions is deliberately left unanswered here. Whether booking a retry against an
/// identity that is already <c>Handled</c> should resurrect it turns out not to be a document-store question
/// at all: every store resurrects it today except PostgreSQL with <c>EnableInboxPartitioning</c>, because the
/// <c>status &lt;&gt; 'Handled'</c> exclusion in <c>MessageDatabase.ScheduleExecutionSql</c> is gated on
/// partitioning. That is the open decision GH-4216 asks to be made deliberately rather than as a side effect,
/// so it is not pinned either way by this store's copy of it.
/// </summary>
[Collection("cosmosdb")]
public class scheduled_promotion_semantics : IAsyncLifetime
{
    private readonly AppFixture _fixture;
    private IHost _host = null!;
    private CosmosDbMessageStore thePersistence = null!;

    public scheduled_promotion_semantics(AppFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        await _fixture.ClearAll();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UseCosmosDbPersistence(AppFixture.DatabaseName);
                opts.Services.AddSingleton(_fixture.Client);

                opts.ListenAtPort(PortFinder.GetAvailablePort()).UseDurableInbox();
            }).StartAsync(TestContext.Current.CancellationToken);

        thePersistence = _host.Services.GetRequiredService<IMessageStore>().As<CosmosDbMessageStore>();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(TestContext.Current.CancellationToken);
        _host.Dispose();
    }

    private async Task<Envelope?> rowFor(Envelope envelope)
    {
        var all = await thePersistence.Admin.AllIncomingAsync();
        return all.SingleOrDefault(x => x.Id == envelope.Id && x.Destination == envelope.Destination);
    }

    /// <summary>
    /// The relational stores can hold two rows for one identity and have to pick a survivor. CosmosDb cannot:
    /// the identity is the document id and the destination is the partition key, so a second receipt is
    /// rejected at the door with a 409 and the scheduled copy is simply the only copy. This pins that
    /// structural guarantee, because it is the reason CosmosDb needs no
    /// <c>discardSupersededScheduledEnvelopes</c> equivalent -- if the duplicate ever started being accepted,
    /// the store would silently acquire the very state that bug class lives in.
    ///
    /// The scheduled time is deliberately in the future: this is about identity, not dueness, and a due row
    /// would race the durability agent's promotion.
    /// </summary>
    [Fact]
    public async Task a_redelivery_while_scheduled_for_retry_is_rejected_as_a_duplicate()
    {
        var scheduled = ObjectMother.Envelope();
        scheduled.Status = EnvelopeStatus.Incoming;
        scheduled.ScheduledTime = DateTimeOffset.UtcNow.AddHours(1);

        await thePersistence.Inbox.StoreIncomingAsync(scheduled);
        await thePersistence.Inbox.ScheduleExecutionAsync(scheduled);

        var redelivered = ObjectMother.Envelope();
        redelivered.Id = scheduled.Id;
        redelivered.Destination = scheduled.Destination;
        redelivered.Status = EnvelopeStatus.Incoming;

        await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(() =>
            thePersistence.Inbox.StoreIncomingAsync(redelivered));

        var row = await rowFor(scheduled);
        row.ShouldNotBeNull();
        row.Status.ShouldBe(EnvelopeStatus.Scheduled,
            "The scheduled copy is the only copy, so the redelivery must leave it exactly as it was.");
    }

    /// <summary>
    /// The CosmosDb twin of <c>MessageStoreCompliance.scheduled_poll_stamps_envelope_with_originating_store</c>
    /// -- a contract this store inherits and then skips, because it is not an <see cref="IMessageDatabase" />.
    ///
    /// <c>Envelope.Store</c> is an in-memory-only reference that does not survive persistence, and
    /// <c>DelegatingMessageInbox</c> routes every write on it, falling back to the MAIN store when it is null.
    /// For an ancillary CosmosDb store that is the whole bug: mark-as-handled is issued against the wrong
    /// store, matches nothing, and the row is promoted again on every pass -- forever. Every relational store
    /// stamps this in its own poller with a GH-2576 comment; this pins the CosmosDb twin.
    ///
    /// Asserted end to end through a real promotion rather than at a seam, because the promotion lives inside
    /// the durability agent's timer loop and the stamping is only observable on the envelope it dispatches.
    /// </summary>
    [Fact]
    public async Task a_promoted_envelope_carries_the_store_it_came_from()
    {
        var catcher = new PromotedMessageCatcher();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobFirstExecution = 100.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();

                opts.Services.AddSingleton(catcher);

                opts.UseCosmosDbPersistence(AppFixture.DatabaseName);
                opts.Services.AddSingleton(_fixture.Client);

                opts.Discovery.DisableConventionalDiscovery().IncludeType<PromotedMessageHandler>();
                opts.Publish(x => x.Message<PromotedMessage>().ToLocalQueue("promoted").UseDurableInbox());

                opts.Transports.NodeControlEndpoint =
                    opts.Transports.GetOrCreateEndpoint(new Uri($"tcp://localhost:{PortFinder.GetAvailablePort()}"));
            }).StartAsync(TestContext.Current.CancellationToken);

        var expected = host.Services.GetRequiredService<IMessageStore>();

        await host.Services.GetRequiredService<IMessageContext>()
            .ScheduleAsync(new PromotedMessage(1), 1.Seconds());

        var completed = await Task.WhenAny(catcher.Source.Task,
            Task.Delay(60.Seconds(), TestContext.Current.CancellationToken));

        (completed == catcher.Source.Task).ShouldBeTrue("The scheduled message was never promoted and handled");

        var promoted = await catcher.Source.Task;

        promoted.Store.ShouldBe(expected,
            "Promoted envelopes must be stamped with the store they came from so downstream " +
            "mark-as-handled / inbox writes route back to the correct store. See GH-2576.");
    }

    /// <summary>
    /// GH-4711, and the premise the issue could not settle from source. <c>CosmosDbDurabilityAgent</c>
    /// enqueues EVERY promoted envelope onto this node's own local queue rather than routing by
    /// <c>Destination</c> the way every relational store's poller does, which looks like it should mis-route a
    /// scheduled message addressed to an external endpoint.
    ///
    /// <para>It does not, because no such row ever reaches the poller. A delayed message bound for an external
    /// endpoint parks as a <c>ScheduledEnvelope</c> WRAPPER addressed to this node's own
    /// <c>local://durable/</c> -- so enqueuing it locally is precisely what has to happen, and
    /// <c>ScheduledSendEnvelopeHandler</c> then unwraps it and sends it on. Asserted here rather than reasoned
    /// about, and asserted on the SHAPE so that a future change which parked a genuinely remote address here
    /// would make this fail instead of passing silently.</para>
    ///
    /// <para>Scheduled an hour out on purpose: this is about where the row lands, not about dueness, and a due
    /// row would race the durability agent.</para>
    /// </summary>
    [Fact]
    public async Task a_delayed_external_send_parks_as_a_wrapper_rather_than_under_its_destination()
    {
        var port = PortFinder.GetAvailablePort();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UseCosmosDbPersistence(AppFixture.DatabaseName);
                opts.Services.AddSingleton(_fixture.Client);

                // Deliberately NOT UseDurableOutbox(): a durable destination schedules in the OUTBOX and never
                // reaches the scheduled inbox poller this test is about.
                opts.PublishMessage<ExternallyBoundMessage>().ToPort(port);

                opts.Policies.DisableConventionalLocalRouting();
                opts.Discovery.DisableConventionalDiscovery();

                opts.Transports.NodeControlEndpoint =
                    opts.Transports.GetOrCreateEndpoint(new Uri($"tcp://localhost:{PortFinder.GetAvailablePort()}"));
            }).StartAsync(TestContext.Current.CancellationToken);

        await host.Services.GetRequiredService<IMessageContext>()
            .ScheduleAsync(new ExternallyBoundMessage(1), 1.Hours());

        var rows = await thePersistence.Admin.AllIncomingAsync();
        var row = rows.ShouldHaveSingleItem();

        row.MessageType.ShouldBe(TransportConstants.ScheduledEnvelope);
        row.Destination.ShouldBe(new Uri("local://durable/"));

        // The crux of GH-4711: nothing is parked under the external address, so a poller that enqueues
        // everything locally is not mis-routing anything.
        row.Destination.ShouldNotBe(new Uri($"tcp://localhost:{port}"));
    }

    /// <summary>
    /// GH-4711, the consequence. The promoted wrapper is unwrapped and delivered to the destination NODE, not
    /// executed on the promoting one. The second host is what makes the claim falsifiable: "it did not run
    /// here" is equally true of an envelope that was silently dropped, so something has to prove it arrived.
    /// </summary>
    [Fact]
    public async Task the_promoted_wrapper_is_unwrapped_and_delivered_to_its_destination()
    {
        var port = PortFinder.GetAvailablePort();
        var catcher = new PromotedMessageCatcher();

        // ONLY this host can handle the message, so "handled" and "handled here" cannot be confused.
        using var receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddSingleton(catcher);
                opts.ListenAtPort(port);
                opts.Discovery.DisableConventionalDiscovery().IncludeType<ExternallyBoundMessageHandler>();
            }).StartAsync(TestContext.Current.CancellationToken);

        using var promoter = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobFirstExecution = 100.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();

                opts.UseCosmosDbPersistence(AppFixture.DatabaseName);
                opts.Services.AddSingleton(_fixture.Client);

                opts.PublishMessage<ExternallyBoundMessage>().ToPort(port);

                opts.Policies.DisableConventionalLocalRouting();
                opts.Discovery.DisableConventionalDiscovery();

                opts.Transports.NodeControlEndpoint =
                    opts.Transports.GetOrCreateEndpoint(new Uri($"tcp://localhost:{PortFinder.GetAvailablePort()}"));
            }).StartAsync(TestContext.Current.CancellationToken);

        await promoter.Services.GetRequiredService<IMessageContext>()
            .ScheduleAsync(new ExternallyBoundMessage(2), 1.Seconds());

        var completed = await Task.WhenAny(catcher.Source.Task,
            Task.Delay(60.Seconds(), TestContext.Current.CancellationToken));

        (completed == catcher.Source.Task).ShouldBeTrue(
            "The promoted scheduled send never reached the listening node");
    }
}

public record ExternallyBoundMessage(int Id);

/// <summary>
/// Registered ONLY on the receiving host in the GH-4711 test, so a caught message proves the envelope crossed
/// the wire rather than being executed by the node that promoted it.
/// </summary>
public class ExternallyBoundMessageHandler
{
    public static void Handle(ExternallyBoundMessage message, Envelope envelope, PromotedMessageCatcher catcher)
    {
        catcher.Source.TrySetResult(envelope);
    }
}
