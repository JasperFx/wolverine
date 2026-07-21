using System.Text;
using NATS.Client.Core;
using NATS.Client.ObjectStore;
using NATS.Net;
using Shouldly;
using Wolverine.Persistence;

namespace Wolverine.ClaimCheck.Nats.Tests;

public class NatsObjectStoreClaimCheckStoreTests : IAsyncLifetime
{
    // Each test class gets its own bucket so parallel classes / re-runs never collide on
    // object names, mirroring the Amazon S3 backend tests.
    private readonly string _bucketName = "claimcheck" + Guid.NewGuid().ToString("N");
    private NatsConnection _connection = null!;
    private NatsObjectStoreClaimCheckStore _store = null!;

    public async Task InitializeAsync()
    {
        if (!NatsServer.IsRunning)
        {
            return;
        }

        _connection = new NatsConnection(new NatsOpts { Url = NatsServer.Url });
        await _connection.ConnectAsync();

        _store = new NatsObjectStoreClaimCheckStore(_connection, _bucketName);
    }

    public async Task DisposeAsync()
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

    [NatsFact]
    public async Task round_trip_store_load_delete()
    {
        var payload = Encoding.UTF8.GetBytes("hello, claim check world");

        var token = await _store.StoreAsync(payload, "text/plain");

        token.Id.ShouldNotBeNullOrWhiteSpace();
        token.ContentType.ShouldBe("text/plain");
        token.Length.ShouldBe(payload.Length);

        var loaded = await _store.LoadAsync(token);
        loaded.ToArray().ShouldBe(payload);

        await _store.DeleteAsync(token);

        // After delete, loading the object should fail with a not-found error.
        await Should.ThrowAsync<NatsObjNotFoundException>(async () => await _store.LoadAsync(token));
    }

    [NatsFact]
    public async Task delete_is_idempotent_for_missing_object()
    {
        var token = new ClaimCheckToken("doesnotexist" + Guid.NewGuid().ToString("N"), "text/plain", 0);

        // Should not throw even though the object was never created.
        await _store.DeleteAsync(token);
    }

    [NatsFact]
    public async Task load_returns_exact_payload_bytes()
    {
        // Binary payload with zero bytes and high bits set to catch any encoding-related
        // corruption in the round-trip.
        var payload = new byte[256];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }

        var token = await _store.StoreAsync(payload, "application/octet-stream");
        var loaded = await _store.LoadAsync(token);

        loaded.Length.ShouldBe(payload.Length);
        loaded.ToArray().ShouldBe(payload);
    }

    [NatsFact]
    public async Task second_store_reuses_the_bucket()
    {
        // Two stores against the same bucket exercise the create-or-get resolution path
        // (first call creates the bucket, subsequent calls fetch it).
        var first = await _store.StoreAsync(Encoding.UTF8.GetBytes("one"), "text/plain");
        var second = await _store.StoreAsync(Encoding.UTF8.GetBytes("two"), "text/plain");

        (await _store.LoadAsync(first)).ToArray().ShouldBe(Encoding.UTF8.GetBytes("one"));
        (await _store.LoadAsync(second)).ToArray().ShouldBe(Encoding.UTF8.GetBytes("two"));
    }
}
