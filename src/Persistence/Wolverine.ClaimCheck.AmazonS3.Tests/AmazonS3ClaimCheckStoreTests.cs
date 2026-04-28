using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Shouldly;
using Wolverine.Persistence;

namespace Wolverine.ClaimCheck.AmazonS3.Tests;

public class AmazonS3ClaimCheckStoreTests : IAsyncLifetime
{
    // Each test class gets its own bucket so parallel test classes / re-runs
    // never collide on object keys, mirroring the Azure Blob backend tests.
    private readonly string _bucketName = "claim-check-tests-" + Guid.NewGuid().ToString("N");
    private AmazonS3Client _client = null!;
    private AmazonS3ClaimCheckStore _store = null!;

    public async Task InitializeAsync()
    {
        if (!LocalStack.IsRunning)
        {
            return;
        }

        _client = LocalStack.CreateClient();

        await _client.PutBucketAsync(new PutBucketRequest
        {
            BucketName = _bucketName
        });

        _store = new AmazonS3ClaimCheckStore(_client, _bucketName);
    }

    public async Task DisposeAsync()
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            // Empty the bucket first; S3 won't delete a non-empty bucket.
            var listed = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucketName
            });

            if (listed.S3Objects is { Count: > 0 })
            {
                await _client.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = _bucketName,
                    Objects = listed.S3Objects.Select(o => new KeyVersion { Key = o.Key }).ToList()
                });
            }

            await _client.DeleteBucketAsync(new DeleteBucketRequest
            {
                BucketName = _bucketName
            });
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

    [LocalStackFact]
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
        var exists = await ObjectExistsAsync(token.Id);
        exists.ShouldBeFalse();
    }

    [LocalStackFact]
    public async Task uploads_use_supplied_content_type()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        const string contentType = "application/x-wolverine-test";

        var token = await _store.StoreAsync(payload, contentType);

        var metadata = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = _bucketName,
            Key = token.Id
        });

        metadata.Headers.ContentType.ShouldBe(contentType);
    }

    [LocalStackFact]
    public async Task delete_is_idempotent_for_missing_object()
    {
        var token = new ClaimCheckToken("does-not-exist-" + Guid.NewGuid().ToString("N"), "text/plain", 0);

        // Should not throw even though the object was never created — S3 returns
        // 204 No Content for DeleteObject on a missing key.
        await _store.DeleteAsync(token);
    }

    [LocalStackFact]
    public async Task load_returns_exact_payload_bytes()
    {
        // Use a binary payload that includes zero bytes and high bits set so
        // we catch any encoding-related corruption in the round-trip.
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

    private async Task<bool> ObjectExistsAsync(string key)
    {
        try
        {
            await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucketName,
                Key = key
            });
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
