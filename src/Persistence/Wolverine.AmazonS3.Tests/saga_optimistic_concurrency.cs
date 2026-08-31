using Amazon.S3;
using IntegrationTests;
using Shouldly;
using Wolverine.AmazonS3.Internals;

namespace Wolverine.AmazonS3.Tests;

/// <summary>
/// GH-4160. The shipped saga compliance specs are entirely sequential, so every one of them passes
/// against a plain PutObject that silently loses concurrent updates. A saga is a read-modify-write by
/// definition, so this is the property that actually makes S3 saga storage safe, and it needs its own
/// test or it is a guarantee nobody checked.
/// </summary>
/// <remarks>
/// Two <see cref="S3DocumentSession" /> instances, because generated code builds one session per
/// handler invocation — so two sessions is exactly two concurrent messages for one saga, without
/// having to race real threads to observe it.
/// </remarks>
public class saga_optimistic_concurrency : IAsyncLifetime
{
    private const string Bucket = "wolverine-s3-saga-concurrency";

    private AmazonS3Client _client = null!;
    private AmazonS3Configuration _configuration = null!;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipUnless(LocalStack.IsRunning, LocalStack.SkipReason);

        _client = LocalStack.CreateClient();

        try
        {
            await _client.PutBucketAsync(Bucket);
        }
        catch (AmazonS3Exception e) when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
        }

        _configuration = new AmazonS3Configuration();
        _configuration.Saga<ConcurrentSaga>(x =>
        {
            x.BucketName = Bucket;
            x.KeyFor = ctx => $"concurrency/{ctx.Id}.json";
        });
    }

    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        return ValueTask.CompletedTask;
    }

    private S3DocumentSession session() => new(_client, _configuration);

    [LocalStackFact]
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

    [LocalStackFact]
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

    [LocalStackFact]
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
}

public class ConcurrentSaga : Saga
{
    public string Id { get; set; } = null!;
    public int Count { get; set; }
}
