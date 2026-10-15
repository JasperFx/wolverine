using JasperFx.Core;
using JasperFx.Core.Reflection;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ComplianceTests;
using Wolverine.RavenDb;
using Wolverine.RavenDb.Internals;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Scheduled;
using Wolverine.Tracking;
using Wolverine.Transports.Stub;
using Wolverine.Util;

namespace RavenDbTests;

/// <summary>
/// GH-5036. Under DurabilityMode.ServerlessWithOutbox there is no durability agent and no scheduled job polling,
/// so the durability pass is what releases due scheduled messages, recovers incoming documents and deletes the
/// expired ones on RavenDb, the same as on the relational stores (GH-5028)
/// </summary>
[Collection("raven")]
public class durability_pass_steps_5036 : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;
    private IDocumentStore theDocumentStore = null!;
    private IHost theHost = null!;
    private RavenDbMessageStore theStore = null!;

    public durability_pass_steps_5036(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        theDocumentStore = _fixture.StartRavenStore();
        RavenDurabilityPassMessageHandler.Handled.Clear();

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                // The rows these tests store as received were received at this listener's address. A due scheduled row
                // parked under an address with no listener is a scheduled send the pass forwards to that endpoint
                opts.ListenForMessagesFrom("stub://inbox");
                opts.Durability.DeadLetterQueueExpirationEnabled = true;
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(RavenDurabilityPassMessageHandler));

                opts.Services.AddSingleton(theDocumentStore);
                opts.UseRavenDbPersistence();

                opts.PublishMessage<RavenStaleOutgoing>().To("stub://pre-upgrade");
            }).StartAsync(Token);

        theStore = theHost.GetRuntime().Storage.As<RavenDbMessageStore>();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
        theDocumentStore.Dispose();
    }

    private IReadOnlyList<Envelope> sentTo(string queueName)
    {
        return theHost.GetRuntime().Options.Transports.GetOrCreate<StubTransport>().Endpoints[queueName].Sent
            .ToArray();
    }

    private Envelope localEnvelope(EnvelopeStatus status)
    {
        var envelope = new Envelope(new RavenDurabilityPassMessage(Guid.NewGuid()))
        {
            Destination = "stub://inbox".ToUri(),
            MessageType = typeof(RavenDurabilityPassMessage).ToMessageTypeName(),
            ContentType = EnvelopeConstants.JsonContentType,
            Status = status,
            OwnerId = 0
        };

        envelope.Data = theHost.GetRuntime().Options.DefaultSerializer.Write(envelope);
        return envelope;
    }

    private async Task<Envelope> storeDueScheduledSendAsync(string destination)
    {
        var inner = ObjectMother.Envelope();
        inner.Destination = destination.ToUri();
        inner.DeliverBy = null;
        inner.ScheduledTime = DateTimeOffset.UtcNow.Subtract(1.Minutes());

        await theStore.Inbox.StoreIncomingAsync(inner.ForScheduledSend(null));

        return inner;
    }

    private async Task<List<T>> documentsAsync<T>()
    {
        using var session = theDocumentStore.OpenAsyncSession();
        return await session.Query<T>().Customize(x => x.WaitForNonStaleResults()).ToListAsync(Token);
    }

    private async Task<Dictionary<Guid, EnvelopeStatus>> inboxStatusesAsync()
    {
        return (await documentsAsync<IncomingMessage>()).ToDictionary(x => x.EnvelopeId, x => x.Status);
    }

    [Fact]
    public async Task one_pass_releases_due_scheduled_messages_and_recovers_incoming_documents()
    {
        // A scheduled send to an external endpoint
        var external = await storeDueScheduledSendAsync("stub://scheduled");

        // A local message that is due, and one that is not
        var dueLocal = localEnvelope(EnvelopeStatus.Scheduled);
        dueLocal.ScheduledTime = DateTimeOffset.UtcNow.Subtract(1.Minutes());
        await theStore.Inbox.StoreIncomingAsync(dueLocal);

        var notDue = localEnvelope(EnvelopeStatus.Scheduled);
        notDue.ScheduledTime = DateTimeOffset.UtcNow.Add(1.Hours());
        await theStore.Inbox.StoreIncomingAsync(notDue);

        // An incoming document persisted but never handled, and one still owned by a node
        var recoverable = localEnvelope(EnvelopeStatus.Incoming);
        await theStore.Inbox.StoreIncomingAsync(recoverable);

        var owned = localEnvelope(EnvelopeStatus.Incoming);
        owned.OwnerId = 5;
        await theStore.Inbox.StoreIncomingAsync(owned);

        var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);

        report.FailedSteps.ShouldBe(0);
        report.ScheduledReleased.ShouldBe(2);
        report.IncomingRecovered.ShouldBe(1);
        report.Remaining.ShouldBe(0);

        sentTo("scheduled").Single().Id.ShouldBe(external.Id);

        RavenDurabilityPassMessageHandler.Handled.OrderBy(x => x).ShouldBe(new[]
        {
            dueLocal.Message!.As<RavenDurabilityPassMessage>().Id,
            recoverable.Message!.As<RavenDurabilityPassMessage>().Id
        }.OrderBy(x => x));

        var statuses = await inboxStatusesAsync();
        statuses[dueLocal.Id].ShouldBe(EnvelopeStatus.Handled);
        statuses[recoverable.Id].ShouldBe(EnvelopeStatus.Handled);
        statuses[notDue.Id].ShouldBe(EnvelopeStatus.Scheduled);
        statuses[owned.Id].ShouldBe(EnvelopeStatus.Incoming);

        // Nothing is left for a second pass
        var second = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);
        second.ScheduledReleased.ShouldBe(0);
        second.IncomingRecovered.ShouldBe(0);
        sentTo("scheduled").Count.ShouldBe(1);
    }

    [Fact]
    public async Task due_scheduled_messages_are_released_in_batches()
    {
        for (var i = 0; i < 5; i++)
        {
            await storeDueScheduledSendAsync("stub://batched-scheduled");
        }

        var report = await theHost.RunDurabilityPassAsync(new DurabilityPassOptions
        {
            MaxDuration = 30.Seconds(),
            BatchSize = 2
        }, Token);

        report.ScheduledReleased.ShouldBe(5);
        report.HasMoreWork.ShouldBeFalse();

        var step = report.Stores.Single(x => x.StoreUri == theStore.Uri).Steps
            .Single(x => x.StepName == "Release due scheduled messages");
        step.Calls.ShouldBe(3);

        sentTo("batched-scheduled").Count.ShouldBe(5);
    }

    [Fact]
    public async Task a_pass_deletes_expired_handled_documents_and_dead_letters()
    {
        var expired = localEnvelope(EnvelopeStatus.Handled);
        expired.KeepUntil = DateTimeOffset.UtcNow.Subtract(1.Hours());
        await theStore.Inbox.StoreIncomingAsync(expired);

        var retained = localEnvelope(EnvelopeStatus.Handled);
        retained.KeepUntil = DateTimeOffset.UtcNow.Add(1.Hours());
        await theStore.Inbox.StoreIncomingAsync(retained);

        // Stored already Handled with no keep-until of its own, the way inbox deduplication writes them
        var deduplicated = localEnvelope(EnvelopeStatus.Handled);
        await theStore.Inbox.StoreIncomingAsync(deduplicated);

        var deadLetter = localEnvelope(EnvelopeStatus.Incoming);
        deadLetter.DeliverBy = DateTimeOffset.UtcNow.Subtract(1.Hours());
        await theStore.Inbox.StoreIncomingAsync(deadLetter);
        await theStore.Inbox.MoveToDeadLetterStorageAsync(deadLetter, new DivideByZeroException());

        (await documentsAsync<IncomingMessage>())
            .Single(x => x.EnvelopeId == deduplicated.Id).KeepUntil.ShouldNotBeNull();

        var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);

        report.FailedSteps.ShouldBe(0);
        report.ExpiredDeleted.ShouldBe(2);

        (await inboxStatusesAsync()).Keys.OrderBy(x => x)
            .ShouldBe(new[] { retained.Id, deduplicated.Id }.OrderBy(x => x));
        (await documentsAsync<DeadLetterMessage>()).ShouldBeEmpty();
    }

    [Fact]
    public async Task the_pass_lock_is_renewed_while_it_is_held()
    {
        theStore.DurabilityPassLockTtl = 2.Seconds();

        var held = await theStore.TryAcquireDurabilityPassLockAsync(Token);
        held.ShouldNotBeNull();

        // Well past the TTL, and the lock is still held because it was renewed
        await Task.Delay(5.Seconds(), Token);
        (await theStore.TryAcquireDurabilityPassLockAsync(Token)).ShouldBeNull();

        await held.DisposeAsync();

        var next = await theStore.TryAcquireDurabilityPassLockAsync(Token);
        next.ShouldNotBeNull();
        await next.DisposeAsync();
    }

    [Fact]
    public async Task an_outbox_document_written_before_timestamp_existed_is_recovered_as_stale()
    {
        var envelope = new Envelope(new RavenStaleOutgoing(Guid.NewGuid()))
        {
            Destination = "stub://pre-upgrade".ToUri(),
            ContentType = EnvelopeConstants.JsonContentType
        };
        envelope.Data = theHost.GetRuntime().Options.DefaultSerializer.Write(envelope);

        using (var session = theDocumentStore.OpenAsyncSession())
        {
            await new RavenDbEnvelopeTransaction(session, new MessageContext(theHost.GetRuntime()))
                .PersistOutgoingAsync(envelope);
            await session.SaveChangesAsync(Token);
        }

        // Take the property off the document altogether, as a document written by an older version has it
        var operation = await theDocumentStore.Operations.SendAsync(new PatchOperation(envelope.Id.ToString(), null,
            new PatchRequest { Script = "delete this.Timestamp;" }), token: Token);
        operation.ShouldBe(PatchStatus.Patched);

        var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);

        report.OutgoingSent.ShouldBe(1);
        sentTo("pre-upgrade").Single().Id.ShouldBe(envelope.Id);
        (await documentsAsync<OutgoingMessage>()).ShouldBeEmpty();
    }
}

public record RavenDurabilityPassMessage(Guid Id);

// Kept out of conventional discovery, where it would give the other tests' hosts an extra handler. The
// durability pass host includes it explicitly
[WolverineIgnore]
public static class RavenDurabilityPassMessageHandler
{
    public static readonly List<Guid> Handled = new();

    public static void Handle(RavenDurabilityPassMessage message)
    {
        lock (Handled)
        {
            Handled.Add(message.Id);
        }
    }
}

public record RavenStaleOutgoing(Guid Id);
