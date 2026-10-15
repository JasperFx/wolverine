using System.Net;
using JasperFx.Core;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Shouldly;
using Wolverine;
using Wolverine.CosmosDb;
using Wolverine.CosmosDb.Internals;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports.Stub;

namespace CosmosDbTests;

/// <summary>
/// GH-5016. Cosmos DB under DurabilityMode.ServerlessWithOutbox: outgoing envelopes are written to the outbox
/// as owned by any node (0), sent inline as soon as the handler's writes are done with their outbox documents
/// deleted, and anything left behind is recovered by a durability pass.
/// </summary>
[Collection("cosmosdb")]
public class serverless_with_outbox_5016 : IAsyncLifetime
{
    private readonly AppFixture _fixture;
    private IHost theHost = null!;

    public serverless_with_outbox_5016(AppFixture fixture)
    {
        _fixture = fixture;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _fixture.ClearAll();
    }

    private async Task startHostAsync(bool enableOutgoingTimestampIndex = false)
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;

                // Makes no difference to Cosmos DB, whose containers index every property by default
                opts.Durability.EnableOutgoingTimestampIndex = enableOutgoingTimestampIndex;

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(ServerlessCosmosHandler));

                opts.Services.AddSingleton(_fixture.Client);
                opts.UseCosmosDbPersistence(AppFixture.DatabaseName);
                opts.Policies.AutoApplyTransactions();

                opts.PublishMessage<ServerlessCosmosCascaded>().To("stub://serverless-cosmos");
            }).StartAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (theHost != null)
        {
            await theHost.StopAsync();
            theHost.Dispose();
        }
    }

    private IReadOnlyList<Envelope> sent()
    {
        return theHost.GetRuntime().Options.Transports.GetOrCreate<StubTransport>()
            .Endpoints["serverless-cosmos"].Sent.ToArray();
    }

    private async Task<IReadOnlyList<OutgoingMessage>> outboxDocumentsAsync()
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.docType = @docType")
            .WithParameter("@docType", DocumentTypes.Outgoing);

        var results = new List<OutgoingMessage>();
        using var iterator = _fixture.Container.GetItemQueryIterator<OutgoingMessage>(query);
        while (iterator.HasMoreResults)
        {
            results.AddRange(await iterator.ReadNextAsync(Token));
        }

        return results;
    }

    // Written to the outbox but never sent, as when the request died after the write and before the inline send
    private async Task<Envelope> persistUnsentEnvelopeAsync()
    {
        var envelope = new Envelope(new ServerlessCosmosCascaded(Guid.NewGuid().ToString()))
        {
            Destination = "stub://serverless-cosmos".ToUri(),
            ContentType = "application/json"
        };
        envelope.Data = theHost.GetRuntime().Options.DefaultSerializer.Write(envelope);

        var context = new MessageContext(theHost.GetRuntime());
        await new CosmosDbEnvelopeTransaction(_fixture.Container, context).PersistOutgoingAsync(envelope);

        return envelope;
    }

    private async Task makeStaleAsync(Envelope envelope)
    {
        var partition = new PartitionKey(envelope.Destination!.ToString());
        var response = await _fixture.Container.ReadItemAsync<OutgoingMessage>($"outgoing|{envelope.Id}", partition,
            cancellationToken: Token);
        var document = response.Resource;
        document.Timestamp = DateTimeOffset.UtcNow.Subtract(2.Hours());
        await _fixture.Container.ReplaceItemAsync(document, document.Id, partition, cancellationToken: Token);
    }

    [Fact]
    public async Task cascaded_messages_are_sent_inline_when_the_handler_completes()
    {
        await startHostAsync();
        var id = Guid.NewGuid().ToString();

        await theHost.MessageBus().InvokeAsync(new ServerlessCosmosCommand(id), Token);

        // The handler's document was written...
        var doc = await _fixture.Container.ReadItemAsync<ServerlessCosmosDoc>(id, new PartitionKey(id),
            cancellationToken: Token);
        doc.StatusCode.ShouldBe(HttpStatusCode.OK);

        // ...and its cascaded message went out before InvokeAsync() returned, with nothing left behind
        sent().Single().Message.ShouldBe(new ServerlessCosmosCascaded(id));
        (await outboxDocumentsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task the_cosmosdb_outbox_sends_inline_on_save_changes()
    {
        await startHostAsync();
        var id = Guid.NewGuid().ToString();

        var outbox = new CosmosDbOutbox(theHost.GetRuntime(), _fixture.Container);

        await _fixture.Container.UpsertItemAsync(new ServerlessCosmosDoc { Id = id, PartitionKey = id },
            new PartitionKey(id), cancellationToken: Token);
        await outbox.PublishAsync(new ServerlessCosmosCascaded(id));

        await outbox.SaveChangesAsync(Token);

        sent().Single().Message.ShouldBe(new ServerlessCosmosCascaded(id));
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
    public async Task a_pass_skips_the_store_while_another_pass_holds_its_lock()
    {
        await startHostAsync();

        var envelope = await persistUnsentEnvelopeAsync();
        await makeStaleAsync(envelope);

        var store = (CosmosDbMessageStore)theHost.GetRuntime().Storage;
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
    public async Task several_sent_documents_are_deleted_in_one_batch()
    {
        await startHostAsync();

        var envelopes = new[]
        {
            await persistUnsentEnvelopeAsync(),
            await persistUnsentEnvelopeAsync(),
            await persistUnsentEnvelopeAsync()
        };
        (await outboxDocumentsAsync()).Count.ShouldBe(3);

        // One of them already gone fails the transactional batch, which falls back to one delete each
        await theHost.GetRuntime().Storage.Outbox.DeleteOutgoingAsync(envelopes[0]);
        await theHost.GetRuntime().Storage.Outbox.DeleteOutgoingAsync(envelopes);

        (await outboxDocumentsAsync()).ShouldBeEmpty();
    }
}

public record ServerlessCosmosCommand(string Id);

public record ServerlessCosmosCascaded(string Id);

public class ServerlessCosmosDoc
{
    [JsonProperty("id")] public string Id { get; set; } = null!;

    [JsonProperty("partitionKey")] public string PartitionKey { get; set; } = null!;
}

public static class ServerlessCosmosHandler
{
    public static async Task<ServerlessCosmosCascaded> Handle(ServerlessCosmosCommand command, Container container)
    {
        await container.UpsertItemAsync(new ServerlessCosmosDoc { Id = command.Id, PartitionKey = command.Id },
            new PartitionKey(command.Id));
        return new ServerlessCosmosCascaded(command.Id);
    }
}
