using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.CosmosDb;
using Wolverine.CosmosDb.Internals;
using Wolverine.Persistence.Durability;
using Wolverine.Tracking;

namespace CosmosDbTests;

/// <summary>
/// GH-5059. Under MessageIdentity.IdOnly an inbox document is partitioned by its identity, so Cosmos DB's
/// per-partition id uniqueness rejects the same id received at another address. Documents written before that were
/// partitioned by destination, and are still found, updated, released and deleted
/// </summary>
[Collection("cosmosdb")]
public class inbox_partitioned_by_identity_5059 : IAsyncLifetime
{
    private readonly AppFixture _fixture;
    private IHost theHost = null!;
    private CosmosDbMessageStore theStore = null!;

    public inbox_partitioned_by_identity_5059(AppFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        await _fixture.ClearAll();

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.MessageIdentity = MessageIdentity.IdOnly;

                opts.UseCosmosDbPersistence(AppFixture.DatabaseName);
                opts.Services.AddSingleton(_fixture.Client);
            }).StartAsync();

        theStore = (CosmosDbMessageStore)theHost.GetRuntime().Storage;
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private static Envelope incoming(Guid? id = null, string destination = "stub://incoming")
    {
        var envelope = ObjectMother.Envelope();
        envelope.Id = id ?? Guid.NewGuid();
        envelope.Destination = new Uri(destination);
        envelope.Status = EnvelopeStatus.Incoming;
        return envelope;
    }

    // As every version before GH-5059 wrote an inbox document: partitioned by its destination
    private async Task storeTheOldWayAsync(Envelope envelope)
    {
        var document = new IncomingMessage(envelope, theStore) { PartitionKey = envelope.Destination!.ToString() };
        await _fixture.Container.CreateItemAsync(document, new PartitionKey(document.PartitionKey));
    }

    private async Task<IncomingMessage?> readAsync(Envelope envelope, string partitionKey)
    {
        try
        {
            var response = await _fixture.Container.ReadItemAsync<IncomingMessage>(theStore.IdentityFor(envelope),
                new PartitionKey(partitionKey));
            return response.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    [Fact]
    public async Task a_new_document_is_partitioned_by_its_identity()
    {
        var envelope = incoming();
        await theStore.Inbox.StoreIncomingAsync(envelope);

        (await readAsync(envelope, theStore.IdentityFor(envelope))).ShouldNotBeNull();
        (await readAsync(envelope, envelope.Destination!.ToString())).ShouldBeNull();
    }

    [Fact]
    public async Task the_same_id_at_another_destination_is_a_duplicate()
    {
        var envelope = incoming();
        await theStore.Inbox.StoreIncomingAsync(envelope);

        var elsewhere = incoming(envelope.Id, "stub://elsewhere");

        (await theStore.Inbox.ExistsAsync(elsewhere, CancellationToken.None)).ShouldBeTrue();
        await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(() => theStore.Inbox.StoreIncomingAsync(elsewhere));
    }

    [Fact]
    public async Task a_document_written_the_old_way_is_found_and_marked_handled_where_it_is()
    {
        var envelope = incoming();
        await storeTheOldWayAsync(envelope);

        (await theStore.Inbox.ExistsAsync(envelope, CancellationToken.None)).ShouldBeTrue();
        (await theStore.Inbox.IsHandledAsync(envelope, CancellationToken.None)).ShouldBeFalse();

        await theStore.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        (await theStore.Inbox.IsHandledAsync(envelope, CancellationToken.None)).ShouldBeTrue();
        (await readAsync(envelope, envelope.Destination!.ToString()))!.Status.ShouldBe(EnvelopeStatus.Handled);
    }

    [Fact]
    public async Task a_document_written_the_old_way_is_rescheduled_and_has_its_attempts_counted()
    {
        var envelope = incoming();
        await storeTheOldWayAsync(envelope);

        envelope.Attempts = 3;
        await theStore.Inbox.IncrementIncomingEnvelopeAttemptsAsync(envelope);

        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(5);
        await theStore.Inbox.ScheduleExecutionAsync(envelope);

        var document = (await readAsync(envelope, envelope.Destination!.ToString()))!;
        document.Attempts.ShouldBe(3);
        document.Status.ShouldBe(EnvelopeStatus.Scheduled);
    }

    [Fact]
    public async Task a_document_written_the_old_way_is_deleted()
    {
        var envelope = incoming();
        await storeTheOldWayAsync(envelope);

        await theStore.Inbox.DeleteIncomingEnvelopeAsync(envelope);

        (await theStore.Inbox.ExistsAsync(envelope, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task a_document_written_the_old_way_is_moved_to_dead_letter_storage()
    {
        var envelope = incoming();
        await storeTheOldWayAsync(envelope);

        await theStore.Inbox.MoveToDeadLetterStorageAsync(envelope, new InvalidOperationException("Boom"));

        (await theStore.Inbox.ExistsAsync(envelope, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task release_and_reassign_reach_documents_in_either_partition()
    {
        var oldWay = incoming();
        oldWay.OwnerId = 7;
        await storeTheOldWayAsync(oldWay);

        var newWay = incoming();
        newWay.OwnerId = 7;
        await theStore.Inbox.StoreIncomingAsync(newWay);

        await theStore.Inbox.ReleaseIncomingAsync(7, new Uri("stub://incoming"));

        (await readAsync(oldWay, oldWay.Destination!.ToString()))!.OwnerId.ShouldBe(0);
        (await readAsync(newWay, theStore.IdentityFor(newWay)))!.OwnerId.ShouldBe(0);

        await theStore.ReassignIncomingAsync(9, [oldWay, newWay]);

        (await readAsync(oldWay, oldWay.Destination!.ToString()))!.OwnerId.ShouldBe(9);
        (await readAsync(newWay, theStore.IdentityFor(newWay)))!.OwnerId.ShouldBe(9);
    }
}
