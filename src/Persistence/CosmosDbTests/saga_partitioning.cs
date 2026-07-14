using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.CosmosDb;
using Wolverine.CosmosDb.Internals;
using Wolverine.Persistence;

namespace CosmosDbTests;

/// <summary>
///     GH-3415. A saga is a user's own POCO, so nothing writes the container's <c>/partitionKey</c> property into
///     it and every saga in the application lands in CosmosDB's single "undefined" logical partition — capped at
///     20 GB and 10,000 RU/s no matter how far the container is scaled out. Opting into
///     <see cref="CosmosDbConfiguration.PartitionSagasById" /> gives each saga a partition of its own, keyed by the
///     saga id, so that every saga operation is a single-partition point operation.
///     <para>
///         Because that moves where a saga document lives, the default has to stay where it was, and these tests
///         pin both.
///     </para>
/// </summary>
[Collection("cosmosdb")]
public class saga_partitioning
{
    private readonly AppFixture _fixture;

    public saga_partitioning(AppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task partitioned_saga_lives_in_the_partition_keyed_by_its_own_id()
    {
        using var host = await buildHostAsync(partitionById: true);

        var id = Guid.NewGuid().ToString();
        await host.MessageBus().InvokeAsync(new StartPartitioned(id));

        // The point read CosmosDB is at its best on: id and partition key are the same value
        var saga = await loadAsync(id, new PartitionKey(id));
        saga.ShouldNotBeNull();

        // ...and the document carries the partition key that put it there
        var document = await readRawAsync(id, new PartitionKey(id));
        document["partitionKey"]!.ToString().ShouldBe(id);

        // ...and it is no longer sharing the undefined partition with every other saga in the application
        (await loadAsync(id, PartitionKey.None)).ShouldBeNull();
    }

    /// <summary>
    ///     Saga documents written before the flag was turned on stay where they are, so the default cannot move —
    ///     an application that upgrades and does not opt in has to keep finding its sagas
    /// </summary>
    [Fact]
    public async Task saga_stays_in_the_undefined_partition_by_default()
    {
        using var host = await buildHostAsync(partitionById: false);

        var id = Guid.NewGuid().ToString();
        await host.MessageBus().InvokeAsync(new StartPartitioned(id));

        (await loadAsync(id, PartitionKey.None)).ShouldNotBeNull();
        (await loadAsync(id, new PartitionKey(id))).ShouldBeNull();
    }

    /// <summary>
    ///     The write goes through the CosmosDB stream API to stamp the partition key onto the document, so the
    ///     whole saga lifecycle — insert, update, delete — has to be re-proven through it
    /// </summary>
    [Fact]
    public async Task partitioned_saga_can_be_updated_and_completed()
    {
        using var host = await buildHostAsync(partitionById: true);

        var id = Guid.NewGuid().ToString();
        await host.MessageBus().InvokeAsync(new StartPartitioned(id));
        await host.MessageBus().InvokeAsync(new IncrementPartitioned(id));
        await host.MessageBus().InvokeAsync(new IncrementPartitioned(id));

        var saga = await loadAsync(id, new PartitionKey(id));
        saga!.Count.ShouldBe(2);

        await host.MessageBus().InvokeAsync(new CompletePartitioned(id));

        (await loadAsync(id, new PartitionKey(id))).ShouldBeNull();
    }

    /// <summary>
    ///     GH-3414's compare-and-swap has to survive the switch to the stream API: the ETag captured on the read
    ///     is still passed back as an IfMatchEtag, and CosmosDB's 412 still surfaces as a SagaConcurrencyException
    /// </summary>
    [Fact]
    public async Task optimistic_concurrency_still_holds_for_a_partitioned_saga()
    {
        using var host = await buildHostAsync(partitionById: true);
        var bus = host.MessageBus();

        var id = Guid.NewGuid().ToString();
        await bus.InvokeAsync(new StartPartitioned(id));

        // Commit a competing revision of the document between this message's read and its write, exactly as a
        // second node handling another message for this saga would
        await Should.ThrowAsync<SagaConcurrencyException>(() =>
            bus.InvokeAsync(new IncrementPartitioned(id) { InterfereEveryAttempt = true }));

        var saga = await loadAsync(id, new PartitionKey(id));
        saga!.Count.ShouldBe(PartitionedSaga.InterferingCount);
    }

    /// <summary>
    ///     A saga can also be written by an explicit storage action rather than by the saga chain, and that write
    ///     has to land in the partition the saga loader will look in — otherwise turning partitioning on would
    ///     quietly hide the saga from every message that follows
    /// </summary>
    [Fact]
    public async Task saga_stored_through_a_storage_action_lands_in_its_own_partition()
    {
        using var host = await buildHostAsync(partitionById: true);

        var id = Guid.NewGuid().ToString();
        await host.MessageBus().InvokeAsync(new StorePartitionedDirectly(id));

        var saga = await loadAsync(id, new PartitionKey(id));
        saga!.Count.ShouldBe(StorePartitionedDirectlyHandler.StoredCount);

        (await loadAsync(id, PartitionKey.None)).ShouldBeNull();
    }

    private Task<IHost> buildHostAsync(bool partitionById)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddSingleton(_fixture.Client);
                opts.UseCosmosDbPersistence(AppFixture.DatabaseName, cosmos =>
                {
                    if (partitionById)
                    {
                        cosmos.PartitionSagasById();
                    }
                });

                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<PartitionedSaga>();
                opts.Discovery.IncludeType(typeof(StorePartitionedDirectlyHandler));
            }).StartAsync();
    }

    private async Task<PartitionedSaga?> loadAsync(string id, PartitionKey partitionKey)
    {
        try
        {
            var response = await _fixture.Container.ReadItemAsync<PartitionedSaga>(id, partitionKey);
            return response.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<Dictionary<string, object>> readRawAsync(string id, PartitionKey partitionKey)
    {
        var response = await _fixture.Container.ReadItemAsync<Dictionary<string, object>>(id, partitionKey);
        return response.Resource;
    }
}

public record StartPartitioned(string Id);

public record IncrementPartitioned(string Id)
{
    /// <summary>
    ///     Commit a competing revision of the saga document on every attempt, so the pending write can never win
    /// </summary>
    public bool InterfereEveryAttempt { get; init; }
}

public record CompletePartitioned(string Id);

public record StorePartitionedDirectly(string Id);

public static class StorePartitionedDirectlyHandler
{
    public const int StoredCount = 5;

    public static IStorageAction<PartitionedSaga> Handle(StorePartitionedDirectly command)
    {
        return Storage.Store(new PartitionedSaga { Id = command.Id, Count = StoredCount });
    }
}

public class PartitionedSaga : Saga
{
    internal const int InterferingCount = 100;

    public string Id { get; set; } = string.Empty;
    public int Count { get; set; }

    public static PartitionedSaga Start(StartPartitioned command)
    {
        return new PartitionedSaga { Id = command.Id };
    }

    public async Task Handle(IncrementPartitioned command, Container container, CancellationToken cancellationToken)
    {
        if (command.InterfereEveryAttempt)
        {
            // Bump the stored document's ETag out from under the saga that is mid-flight. Written the same way
            // Wolverine writes it, so that it lands in the same partition
            await CosmosSagaStorage.UpsertAsync(container,
                new PartitionedSaga { Id = command.Id, Count = InterferingCount }, null, cancellationToken);
        }

        Count++;
    }

    public void Handle(CompletePartitioned command)
    {
        MarkCompleted();
    }
}
