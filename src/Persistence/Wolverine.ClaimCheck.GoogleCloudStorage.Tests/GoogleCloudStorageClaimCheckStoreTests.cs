using System.Net;
using System.Text;
using Google;
using Google.Cloud.Storage.V1;
using Shouldly;
using Wolverine.Persistence;

namespace Wolverine.ClaimCheck.GoogleCloudStorage.Tests;

public class GoogleCloudStorageClaimCheckStoreTests : IAsyncLifetime
{
    private const string ProjectId = "wolverine-test";

    // Each test class gets its own bucket so parallel classes / re-runs never collide on object
    // names, mirroring the Amazon S3 backend tests.
    private readonly string _bucketName = "claim-check-tests-" + Guid.NewGuid().ToString("N");
    private StorageClient _client = null!;
    private GoogleCloudStorageClaimCheckStore _store = null!;

    public async Task InitializeAsync()
    {
        if (!FakeGcs.IsRunning)
        {
            return;
        }

        // Point the client at the fake-gcs-server emulator. BaseUri is used verbatim (it must
        // include the /storage/v1/ service path), and UnauthenticatedAccess skips the Google
        // credential pipeline the emulator doesn't need.
        _client = new StorageClientBuilder
        {
            BaseUri = FakeGcs.EmulatorHost + "/storage/v1/",
            UnauthenticatedAccess = true
        }.Build();

        await _client.CreateBucketAsync(ProjectId, _bucketName);

        _store = new GoogleCloudStorageClaimCheckStore(_client, _bucketName);
    }

    public async Task DisposeAsync()
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            await foreach (var obj in _client.ListObjectsAsync(_bucketName))
            {
                await _client.DeleteObjectAsync(_bucketName, obj.Name);
            }

            await _client.DeleteBucketAsync(_bucketName);
        }
        catch
        {
            // best-effort cleanup
        }
        finally
        {
            _client.Dispose();
        }
    }

    [FakeGcsFact]
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

        // After delete, the object should not exist any more.
        (await ObjectExistsAsync(token.Id)).ShouldBeFalse();
    }

    [FakeGcsFact]
    public async Task uploads_use_supplied_content_type()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        const string contentType = "application/x-wolverine-test";

        var token = await _store.StoreAsync(payload, contentType);

        var metadata = await _client.GetObjectAsync(_bucketName, token.Id);
        metadata.ContentType.ShouldBe(contentType);
    }

    [FakeGcsFact]
    public async Task delete_is_idempotent_for_missing_object()
    {
        var token = new ClaimCheckToken("does-not-exist-" + Guid.NewGuid().ToString("N"), "text/plain", 0);

        // Should not throw even though the object was never created.
        await _store.DeleteAsync(token);
    }

    [FakeGcsFact]
    public async Task load_returns_exact_payload_bytes()
    {
        // Binary payload with zero bytes and high bits set to catch any encoding-related corruption.
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

    private async Task<bool> ObjectExistsAsync(string objectName)
    {
        try
        {
            await _client.GetObjectAsync(_bucketName, objectName);
            return true;
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
