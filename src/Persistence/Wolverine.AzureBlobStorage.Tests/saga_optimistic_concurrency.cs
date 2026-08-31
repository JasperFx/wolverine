using Azure.Storage.Blobs;
using IntegrationTests;
using Shouldly;
using Wolverine.AzureBlobStorage.Internals;

namespace Wolverine.AzureBlobStorage.Tests;

/// <summary>
/// GH-4160. The shipped saga compliance specs are entirely sequential, so every one of them passes
/// against a plain upload that silently loses concurrent updates. A saga is a read-modify-write by
/// definition, so this is the property that actually makes blob saga storage safe, and it needs its
/// own test or it is a guarantee nobody checked.
/// </summary>
/// <remarks>
/// Two <see cref="BlobDocumentSession" /> instances, because generated code builds one session per
/// handler invocation — so two sessions is exactly two concurrent messages for one saga, without
/// having to race real threads to observe it.
/// </remarks>
public class saga_optimistic_concurrency : IAsyncLifetime
{
    private const string Container = "wolverine-blob-saga-concurrency";

    private BlobServiceClient _client = null!;
    private AzureBlobStorageConfiguration _configuration = null!;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipUnless(Azurite.IsRunning, Azurite.SkipReason);

        _client = Azurite.CreateClient();
        await _client.GetBlobContainerClient(Container).CreateIfNotExistsAsync();

        _configuration = new AzureBlobStorageConfiguration();
        _configuration.Saga<ConcurrentSaga>(x =>
        {
            x.ContainerName = Container;
            x.BlobNameFor = ctx => $"concurrency/{ctx.Id}.json";
        });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private BlobDocumentSession session() => new(_client, _configuration);

    [AzuriteFact]
    public async Task the_second_of_two_concurrent_updates_is_refused()
    {
        var id = Guid.NewGuid().ToString("N");
        var ct = TestContext.Current.CancellationToken;

        await session().StoreAsync(new ConcurrentSaga { Id = id, Count = 0 }, null, ct);

        // Two messages for one saga, each with its own session, both reading the same version
        var first = session();
        var second = session();

        var readByFirst = await first.LoadAsync<ConcurrentSaga>(id, null, ct);
        var readBySecond = await second.LoadAsync<ConcurrentSaga>(id, null, ct);

        readByFirst!.Count = 1;
        await first.StoreAsync(readByFirst, null, ct);

        // ...and the second one is now working from a version that no longer exists
        readBySecond!.Count = 2;

        var ex = await Should.ThrowAsync<SagaConcurrencyException>(async () =>
            await second.StoreAsync(readBySecond, null, ct));

        ex.Message.ShouldContain("changed by another message");

        // The first write stands: the loser did not overwrite it, which is the whole point
        var survivor = await session().LoadAsync<ConcurrentSaga>(id, null, ct);
        survivor!.Count.ShouldBe(1);
    }

    /// <summary>
    /// Blob Storage answers a failed <c>If-None-Match: *</c> with 409 rather than the 412 a failed
    /// <c>If-Match</c> gets, so this arm exercises a genuinely different translation from the one above
    /// rather than the same one twice — which is not true of the S3 sibling, where both are 412.
    /// </summary>
    [AzuriteFact]
    public async Task two_sessions_that_both_believe_they_are_starting_the_saga_do_not_both_win()
    {
        var id = Guid.NewGuid().ToString("N");
        var ct = TestContext.Current.CancellationToken;

        // Neither has read anything, so both write with If-None-Match: *
        await session().StoreAsync(new ConcurrentSaga { Id = id, Count = 10 }, null, ct);

        await Should.ThrowAsync<SagaConcurrencyException>(async () =>
            await session().StoreAsync(new ConcurrentSaga { Id = id, Count = 20 }, null, ct));

        (await session().LoadAsync<ConcurrentSaga>(id, null, ct))!.Count.ShouldBe(10);
    }

    [AzuriteFact]
    public async Task one_session_may_write_the_same_saga_twice()
    {
        var id = Guid.NewGuid().ToString("N");
        var ct = TestContext.Current.CancellationToken;

        // A handler that saves, mutates and saves again compares against what it last wrote rather
        // than against what it first read -- otherwise the second write refuses itself.
        var only = session();
        await only.StoreAsync(new ConcurrentSaga { Id = id, Count = 1 }, null, ct);

        var loaded = await only.LoadAsync<ConcurrentSaga>(id, null, ct);
        loaded!.Count = 2;
        await only.StoreAsync(loaded, null, ct);

        loaded.Count = 3;
        await only.StoreAsync(loaded, null, ct);

        (await session().LoadAsync<ConcurrentSaga>(id, null, ct))!.Count.ShouldBe(3);
    }

    /// <summary>
    /// A saga another message deleted out from under this one is a concurrency failure too, not a
    /// resurrection: an <c>If-Match</c> against a blob that is gone is a 412 rather than a 404, so this
    /// falls out of the same translation instead of needing its own.
    /// </summary>
    [AzuriteFact]
    public async Task an_update_of_a_saga_another_message_completed_is_refused()
    {
        var id = Guid.NewGuid().ToString("N");
        var ct = TestContext.Current.CancellationToken;

        await session().StoreAsync(new ConcurrentSaga { Id = id, Count = 1 }, null, ct);

        var reader = session();
        var loaded = await reader.LoadAsync<ConcurrentSaga>(id, null, ct);

        await session().DeleteByIdAsync<ConcurrentSaga>(id, null, ct);

        loaded!.Count = 2;

        await Should.ThrowAsync<SagaConcurrencyException>(async () =>
            await reader.StoreAsync(loaded, null, ct));
    }

    /// <summary>
    /// The mirror of the whole file: an ordinary DOCUMENT is last-write-wins, so the same shape that
    /// throws above must not throw here. Otherwise a document write could quietly inherit the saga's
    /// conditional path and start refusing perfectly ordinary overwrites.
    /// </summary>
    [AzuriteFact]
    public async Task a_document_is_still_last_write_wins()
    {
        var configuration = new AzureBlobStorageConfiguration();
        configuration.Store<ConcurrentDocument>(x =>
        {
            x.ContainerName = Container;
            x.BlobNameFor = ctx => $"documents/{ctx.Id}.json";
        });

        var id = Guid.NewGuid().ToString("N");
        var ct = TestContext.Current.CancellationToken;

        var first = new BlobDocumentSession(_client, configuration);
        var second = new BlobDocumentSession(_client, configuration);

        await first.StoreAsync(new ConcurrentDocument { Id = id, Count = 1 }, null, ct);
        await second.StoreAsync(new ConcurrentDocument { Id = id, Count = 2 }, null, ct);

        (await first.LoadAsync<ConcurrentDocument>(id, null, ct))!.Count.ShouldBe(2);
    }
}

public class ConcurrentSaga : Saga
{
    public string Id { get; set; } = null!;
    public int Count { get; set; }
}

public class ConcurrentDocument
{
    public string Id { get; set; } = null!;
    public int Count { get; set; }
}
