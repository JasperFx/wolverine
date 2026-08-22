using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.ObjectStore;
using NATS.Net;
using Shouldly;
using Wolverine.Persistence;

namespace Wolverine.ClaimCheck.Nats.Tests;

/// <summary>
/// GH-4006: NATS was the one claim-check backend with no expiration path at all — no native lifecycle
/// and no <see cref="IClaimCheckStoreWithExpiration"/>. Both halves are covered here.
/// </summary>
public class NatsObjectStoreClaimCheckStoreExpirationTests : IAsyncLifetime
{
    private readonly string _bucketName = "claimcheckttl" + Guid.NewGuid().ToString("N");
    private NatsConnection _connection = null!;

    public async ValueTask InitializeAsync()
    {
        if (!NatsServer.IsRunning)
        {
            return;
        }

        _connection = new NatsConnection(new NatsOpts { Url = NatsServer.Url });
        await _connection.ConnectAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            var context = new NatsObjContext(_connection.CreateJetStreamContext());
            await context.DeleteObjectStore(_bucketName, CancellationToken.None);
        }
        catch
        {
            // best-effort cleanup
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }

    private NatsObjectStoreClaimCheckStore storeFor(TimeSpan? maxAge = null)
        => new(_connection, _bucketName, maxAge);

    [NatsFact]
    public async Task deletes_aged_payloads_and_leaves_recent_ones()
    {
        var store = storeFor();

        var old = await store.StoreAsync(new byte[] { 1, 2, 3 }, "text/plain",
            TestContext.Current.CancellationToken);

        // The object's MTime is server-assigned, so age the CUTOFF rather than the object: everything
        // written before "now" is expired, and anything written after it is not.
        await Task.Delay(1200, TestContext.Current.CancellationToken);
        var cutoff = DateTimeOffset.UtcNow;
        await Task.Delay(1200, TestContext.Current.CancellationToken);

        var recent = await store.StoreAsync(new byte[] { 4, 5, 6 }, "text/plain",
            TestContext.Current.CancellationToken);

        var deleted = await store.DeleteExpiredPayloadsAsync(cutoff, 100,
            TestContext.Current.CancellationToken);

        deleted.ShouldBe(1);

        await Should.ThrowAsync<NatsObjNotFoundException>(() => store.LoadAsync(old));

        (await store.LoadAsync(recent, TestContext.Current.CancellationToken)).ToArray()
            .ShouldBe(new byte[] { 4, 5, 6 });
    }

    [NatsFact]
    public async Task honors_the_max_count()
    {
        var store = storeFor();

        for (var i = 0; i < 5; i++)
        {
            await store.StoreAsync(new byte[] { (byte)i }, "text/plain", TestContext.Current.CancellationToken);
        }

        await Task.Delay(1200, TestContext.Current.CancellationToken);

        var deleted = await store.DeleteExpiredPayloadsAsync(DateTimeOffset.UtcNow, 2,
            TestContext.Current.CancellationToken);

        deleted.ShouldBe(2);
    }

    [NatsFact]
    public async Task a_sweep_over_an_empty_bucket_terminates_and_deletes_nothing()
    {
        // Regression guard: without NatsObjListOpts.OnNoData the list enumerator parks waiting for more
        // data once it drains the bucket, which would hang the sweeper forever on an empty bucket.
        var store = storeFor();

        // Force the bucket into existence without storing anything that should be swept.
        await store.DeleteAsync(new ClaimCheckToken("never-written", "text/plain", 0),
            TestContext.Current.CancellationToken);

        var sweep = store.DeleteExpiredPayloadsAsync(DateTimeOffset.UtcNow, 100,
            TestContext.Current.CancellationToken);

        var finished = await Task.WhenAny(sweep, Task.Delay(TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken));

        finished.ShouldBeSameAs(sweep, "the sweep hung on an empty bucket");
        (await sweep).ShouldBe(0);
    }

    [NatsFact]
    public async Task a_repeat_sweep_is_a_no_op()
    {
        var store = storeFor();

        await store.StoreAsync(new byte[] { 1 }, "text/plain", TestContext.Current.CancellationToken);
        await Task.Delay(1200, TestContext.Current.CancellationToken);

        var cutoff = DateTimeOffset.UtcNow;
        (await store.DeleteExpiredPayloadsAsync(cutoff, 100, TestContext.Current.CancellationToken)).ShouldBe(1);
        (await store.DeleteExpiredPayloadsAsync(cutoff, 100, TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [NatsFact]
    public async Task a_non_positive_max_count_deletes_nothing()
    {
        var store = storeFor();

        await store.StoreAsync(new byte[] { 1 }, "text/plain", TestContext.Current.CancellationToken);
        await Task.Delay(1200, TestContext.Current.CancellationToken);

        (await store.DeleteExpiredPayloadsAsync(DateTimeOffset.UtcNow, 0,
            TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [NatsFact]
    public async Task a_configured_max_age_is_applied_to_the_bucket_wolverine_creates()
    {
        var store = storeFor(TimeSpan.FromMinutes(30));

        // Touch the store so the bucket is provisioned with the configured max age.
        await store.StoreAsync(new byte[] { 1 }, "text/plain", TestContext.Current.CancellationToken);

        var js = new NatsJSContext(_connection);
        var stream = await js.GetStreamAsync("OBJ_" + _bucketName, cancellationToken: TestContext.Current.CancellationToken);

        // The object-store bucket's TTL is the underlying JetStream stream's MaxAge.
        stream.Info.Config.MaxAge.ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void rejects_a_non_positive_max_age()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new NatsObjectStoreClaimCheckStore(new NatsObjContext(
                new NatsConnection(new NatsOpts { Url = "nats://127.0.0.1:4222" }).CreateJetStreamContext()),
                "bucket", TimeSpan.Zero));
    }
}
