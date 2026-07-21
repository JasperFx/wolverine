using System.Net;
using Google;
using Google.Cloud.Storage.V1;
using Wolverine.Persistence;

namespace Wolverine.ClaimCheck.GoogleCloudStorage;

/// <summary>
/// Google Cloud Storage backed <see cref="IClaimCheckStore"/>. Each claim check payload is stored
/// as a single object in the configured bucket. The <see cref="ClaimCheckToken.Id"/> maps directly
/// to the GCS object name, and the content type from the token is set on the object.
/// </summary>
public class GoogleCloudStorageClaimCheckStore : IClaimCheckStore
{
    private readonly StorageClient _client;
    private readonly string _bucketName;

    /// <summary>
    /// Create a new claim check store backed by an existing GCS bucket.
    /// </summary>
    /// <param name="client">Configured Google Cloud Storage client.</param>
    /// <param name="bucketName">
    /// Name of the GCS bucket used to hold claim check payloads. The bucket must already exist;
    /// this store does not create it.
    /// </param>
    public GoogleCloudStorageClaimCheckStore(StorageClient client, string bucketName)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (string.IsNullOrWhiteSpace(bucketName))
        {
            throw new ArgumentException("Bucket name must be provided", nameof(bucketName));
        }

        _bucketName = bucketName;
    }

    /// <summary>
    /// The configured storage client. Useful for tests and for callers that need to perform
    /// bucket-level lifecycle operations.
    /// </summary>
    public StorageClient Client => _client;

    /// <summary>The configured bucket name.</summary>
    public string BucketName => _bucketName;

    public async Task<ClaimCheckToken> StoreAsync(
        ReadOnlyMemory<byte> payload,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            throw new ArgumentException("contentType must be provided", nameof(contentType));
        }

        var id = Guid.NewGuid().ToString("N");

        // ReadOnlyMemory<byte> may be backed by memory that doesn't expose an array, so go through
        // ToArray() to hand the client a stable MemoryStream.
        var buffer = payload.ToArray();
        using var stream = new MemoryStream(buffer, writable: false);

        await _client.UploadObjectAsync(_bucketName, id, contentType, stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new ClaimCheckToken(id, contentType, payload.Length);
    }

    public async Task<ReadOnlyMemory<byte>> LoadAsync(
        ClaimCheckToken token,
        CancellationToken cancellationToken = default)
    {
        if (token is null)
        {
            throw new ArgumentNullException(nameof(token));
        }

        // Size the buffer exactly when we know the length so we avoid MemoryStream's doubling growth.
        using var ms = token.Length > 0 ? new MemoryStream((int)token.Length) : new MemoryStream();
        await _client.DownloadObjectAsync(_bucketName, token.Id, ms, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new ReadOnlyMemory<byte>(ms.GetBuffer(), 0, (int)ms.Length);
    }

    public async Task DeleteAsync(ClaimCheckToken token, CancellationToken cancellationToken = default)
    {
        if (token is null)
        {
            throw new ArgumentNullException(nameof(token));
        }

        try
        {
            await _client.DeleteObjectAsync(_bucketName, token.Id, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // Best-effort delete — a missing object is not an error.
        }
    }
}
