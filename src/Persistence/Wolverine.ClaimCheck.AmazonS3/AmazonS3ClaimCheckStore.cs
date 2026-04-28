using Amazon.S3;
using Amazon.S3.Model;
using Wolverine.Persistence;

namespace Wolverine.ClaimCheck.AmazonS3;

/// <summary>
/// Amazon S3 backed <see cref="IClaimCheckStore"/>. Each claim check
/// payload is stored as a single object in the configured bucket. The
/// <see cref="ClaimCheckToken.Id"/> maps directly to the S3 object key.
/// </summary>
public class AmazonS3ClaimCheckStore : IClaimCheckStore
{
    private readonly IAmazonS3 _client;
    private readonly string _bucketName;

    /// <summary>
    /// Create a new claim check store backed by an existing S3 bucket.
    /// </summary>
    /// <param name="client">Configured Amazon S3 client.</param>
    /// <param name="bucketName">
    /// Name of the S3 bucket used to hold claim check payloads. The bucket
    /// must already exist; this store does not create it.
    /// </param>
    public AmazonS3ClaimCheckStore(IAmazonS3 client, string bucketName)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (string.IsNullOrWhiteSpace(bucketName))
        {
            throw new ArgumentException("Bucket name must be provided", nameof(bucketName));
        }

        _bucketName = bucketName;
    }

    /// <summary>
    /// The configured S3 client. Useful for tests and for callers that need
    /// to perform bucket-level lifecycle operations.
    /// </summary>
    public IAmazonS3 Client => _client;

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

        // ReadOnlyMemory<byte> may be backed by something not exposing an
        // array (e.g. native memory), so go through ToArray() so we can hand
        // the AWS SDK a stable MemoryStream.
        var buffer = payload.ToArray();
        using var stream = new MemoryStream(buffer, writable: false);

        var request = new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = id,
            InputStream = stream,
            ContentType = contentType,
            AutoCloseStream = false
        };

        await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);

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

        var request = new GetObjectRequest
        {
            BucketName = _bucketName,
            Key = token.Id
        };

        using var response = await _client.GetObjectAsync(request, cancellationToken).ConfigureAwait(false);
        await using var responseStream = response.ResponseStream;

        // When the server reports a positive ContentLength we can size the
        // buffer exactly and avoid the doubling growth pattern of MemoryStream.
        if (response.ContentLength > 0)
        {
            var size = (int)response.ContentLength;
            var buffer = new byte[size];
            var read = 0;
            while (read < size)
            {
                var n = await responseStream
                    .ReadAsync(buffer.AsMemory(read, size - read), cancellationToken)
                    .ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            return new ReadOnlyMemory<byte>(buffer, 0, read);
        }

        using var ms = new MemoryStream();
        await responseStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return ms.ToArray();
    }

    public async Task DeleteAsync(ClaimCheckToken token, CancellationToken cancellationToken = default)
    {
        if (token is null)
        {
            throw new ArgumentNullException(nameof(token));
        }

        var request = new DeleteObjectRequest
        {
            BucketName = _bucketName,
            Key = token.Id
        };

        // S3's DeleteObject is idempotent — deleting a key that doesn't exist
        // returns 204 No Content, so no try/catch needed for "missing" cases.
        await _client.DeleteObjectAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
