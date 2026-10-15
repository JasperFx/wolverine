using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.RavenDb;
using Wolverine.RavenDb.Internals;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports.Stub;

namespace RavenDbTests;

/// <summary>
/// GH-5016. RavenDb under DurabilityMode.ServerlessWithOutbox: outgoing envelopes are persisted in the RavenDb
/// session as owned by any node (0), sent inline as soon as that session commits with their outbox documents
/// deleted, and anything left behind is recovered by a durability pass.
/// </summary>
[Collection("raven")]
public class serverless_with_outbox_5016 : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;
    private IDocumentStore theStore = null!;
    private IHost theHost = null!;

    public serverless_with_outbox_5016(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        theStore = _fixture.StartRavenStore();
        return ValueTask.CompletedTask;
    }

    private async Task startHostAsync(bool enableOutgoingTimestampIndex = false)
    {
        theHost = await buildHostAsync(enableOutgoingTimestampIndex);
    }

    private Task<IHost> buildHostAsync(bool enableOutgoingTimestampIndex = false, string? serviceName = null)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                if (serviceName != null) opts.ServiceName = serviceName;
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;

                // Makes no difference to RavenDb, which maintains its own auto index for the stale query
                opts.Durability.EnableOutgoingTimestampIndex = enableOutgoingTimestampIndex;

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ServerlessRavenHandler));

                opts.Services.AddSingleton(theStore);
                opts.UseRavenDbPersistence();
                opts.Policies.AutoApplyTransactions();

                opts.PublishMessage<ServerlessRavenCascaded>().To("stub://serverless-raven");
            }).StartAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (theHost != null)
        {
            await theHost.StopAsync();
            theHost.Dispose();
        }

        theStore.Dispose();
    }

    private IReadOnlyList<Envelope> sent()
    {
        return theHost.GetRuntime().Options.Transports.GetOrCreate<StubTransport>()
            .Endpoints["serverless-raven"].Sent.ToArray();
    }

    private async Task<IReadOnlyList<OutgoingMessage>> outboxDocumentsAsync()
    {
        using var session = theStore.OpenAsyncSession();
        return await session.Query<OutgoingMessage>()
            .Customize(x => x.WaitForNonStaleResults())
            .ToListAsync(Token);
    }

    // Persisted through the RavenDb session's outbox but never sent, as when the request died after the
    // commit and before the inline send
    private async Task<Envelope> persistUnsentEnvelopeAsync(string destination = "stub://serverless-raven")
    {
        var envelope = new Envelope(new ServerlessRavenCascaded(Guid.NewGuid().ToString()))
        {
            Destination = destination.ToUri(),
            ContentType = "application/json"
        };
        envelope.Data = theHost.GetRuntime().Options.DefaultSerializer.Write(envelope);

        using var session = theStore.OpenAsyncSession();
        var context = new MessageContext(theHost.GetRuntime());
        await new RavenDbEnvelopeTransaction(session, context).PersistOutgoingAsync(envelope);
        await session.SaveChangesAsync(Token);

        return envelope;
    }

    private async Task makeStaleAsync(Envelope envelope)
    {
        using var session = theStore.OpenAsyncSession();
        var document = await session.LoadAsync<OutgoingMessage>(envelope.Id.ToString(), Token);
        document.Timestamp = DateTimeOffset.UtcNow.Subtract(2.Hours());
        await session.SaveChangesAsync(Token);
    }

    [Fact]
    public async Task cascaded_messages_are_sent_inline_when_the_handler_session_commits()
    {
        await startHostAsync();
        var id = Guid.NewGuid().ToString();

        await theHost.MessageBus().InvokeAsync(new ServerlessRavenCommand(id), Token);

        // The handler's document was committed...
        using (var session = theStore.OpenAsyncSession())
        {
            (await session.LoadAsync<ServerlessRavenDoc>(id, Token)).ShouldNotBeNull();
        }

        // ...and its cascaded message went out before InvokeAsync() returned, with nothing left behind
        sent().Single().Message.ShouldBe(new ServerlessRavenCascaded(id));
        (await outboxDocumentsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task the_ravendb_outbox_sends_inline_when_the_session_commits()
    {
        await startHostAsync();
        var id = Guid.NewGuid().ToString();

        using (var session = theStore.OpenAsyncSession())
        {
            var outbox = new RavenDbOutbox(theHost.GetRuntime(), session);

            await session.StoreAsync(new ServerlessRavenDoc { Id = id }, Token);
            await outbox.PublishAsync(new ServerlessRavenCascaded(id));

            await outbox.SaveChangesAsync(Token);
        }

        sent().Single().Message.ShouldBe(new ServerlessRavenCascaded(id));
        (await outboxDocumentsAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task an_unsent_document_is_owned_by_any_node_and_recovered_by_a_durability_pass_once_stale(
        bool enableOutgoingTimestampIndex)
    {
        await startHostAsync(enableOutgoingTimestampIndex);

        var envelope = await persistUnsentEnvelopeAsync();
        (await outboxDocumentsAsync()).Single().OwnerId.ShouldBe(0);

        // Still within OutboxStaleTime, so a pass leaves it to the request that may still be sending it
        var early = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);
        early.OutgoingSent.ShouldBe(0);
        sent().ShouldBeEmpty();

        await makeStaleAsync(envelope);

        var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);

        report.OutgoingSent.ShouldBe(1);
        report.Remaining.ShouldBe(0);
        report.Stores.Single(x => x.StoreUri == theHost.GetRuntime().Storage.Uri)
            .Lock.ShouldBe(DurabilityPassLockStatus.Acquired);

        sent().Single().Id.ShouldBe(envelope.Id);
        (await outboxDocumentsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task services_sharing_a_database_share_one_pass_lock()
    {
        // GH-5038. The stale queries are not scoped by service, so neither may the lock be
        await startHostAsync();
        using var other = await buildHostAsync(serviceName: "another-service");

        var store = (RavenDbMessageStore)theHost.GetRuntime().Storage;
        var otherStore = (RavenDbMessageStore)other.GetRuntime().Storage;

        var held = await store.TryAcquireDurabilityPassLockAsync(Token);
        held.ShouldNotBeNull();

        (await otherStore.TryAcquireDurabilityPassLockAsync(Token)).ShouldBeNull();

        await held.DisposeAsync();
        await other.StopAsync(Token);
    }

    [Fact]
    public async Task a_pass_skips_the_store_while_another_pass_holds_its_lock()
    {
        await startHostAsync();

        var envelope = await persistUnsentEnvelopeAsync();
        await makeStaleAsync(envelope);

        var store = (RavenDbMessageStore)theHost.GetRuntime().Storage;
        var held = await store.TryAcquireDurabilityPassLockAsync(Token);
        held.ShouldNotBeNull();

        // A second attempt is refused without waiting
        (await store.TryAcquireDurabilityPassLockAsync(Token)).ShouldBeNull();

        var skipped = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);
        skipped.SkippedStores.ShouldContain(store.Uri);
        skipped.OutgoingSent.ShouldBe(0);
        sent().ShouldBeEmpty();

        await held.DisposeAsync();

        var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);
        report.OutgoingSent.ShouldBe(1);
        sent().Single().Id.ShouldBe(envelope.Id);
    }

    [Fact]
    public async Task a_failed_send_restarts_the_documents_stale_clock()
    {
        await startHostAsync();

        // Nothing can be sent there, so the pass fails to send it
        var envelope = await persistUnsentEnvelopeAsync("nowhere://down");
        await makeStaleAsync(envelope);

        var first = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);
        first.OutgoingFailed.ShouldBe(1);

        // GH-5050. Back of the stale order, and not tried again until OutboxStaleTime has gone by
        var restarted = (await outboxDocumentsAsync()).Single();
        restarted.Timestamp.ShouldNotBeNull();
        restarted.Timestamp.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow.Subtract(1.Minutes()));

        var second = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);
        second.OutgoingFailed.ShouldBe(0);
        second.HasMoreWork.ShouldBeFalse();
    }
}

public record ServerlessRavenCommand(string Id);

public record ServerlessRavenCascaded(string Id);

public class ServerlessRavenDoc
{
    public string Id { get; set; } = null!;
}

public static class ServerlessRavenHandler
{
    public static async Task<ServerlessRavenCascaded> Handle(ServerlessRavenCommand command,
        IAsyncDocumentSession session)
    {
        await session.StoreAsync(new ServerlessRavenDoc { Id = command.Id });
        return new ServerlessRavenCascaded(command.Id);
    }
}
