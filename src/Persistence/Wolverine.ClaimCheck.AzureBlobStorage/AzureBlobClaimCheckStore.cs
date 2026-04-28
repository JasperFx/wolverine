using Azure.Storage.Blobs;
using Wolverine.Persistence;
using Azure.Storage.Blobs.Models;

namespace Wolverine.ClaimCheck.AzureBlobStorage;

/// <summary>
/// Azure Blob Storage backed <see cref="IClaimCheckStore"/>. Each
/// claim check payload is stored as a single blob in the configured
/// container. The <see cref="ClaimCheckToken.Id"/> maps directly to
/// the blob name.
/// </summary>
public class AzureBlobClaimCheckStore : IClaimCheckStore
{
    private readonly BlobContainerClient _containerClient;

    public AzureBlobClaimCheckStore(BlobContainerClient containerClient)
    {
        _containerClient = containerClient ?? throw new ArgumentNullException(nameof(containerClient));
    }

    public AzureBlobClaimCheckStore(string connectionString, string containerName)
        : this(new BlobContainerClient(connectionString, containerName))
    {
    }

    /// <summary>
    /// The underlying <see cref="BlobContainerClient"/>. Useful for tests
    /// and for callers that need to perform container-level lifecycle
    /// operations (e.g. creating the container before first use).
    /// </summary>
    public BlobContainerClient ContainerClient => _containerClient;

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
        var blob = _containerClient.GetBlobClient(id);

        var headers = new BlobHttpHeaders { ContentType = contentType };
        var options = new BlobUploadOptions { HttpHeaders = headers };

        // Avoid a copy: BinaryData.FromBytes accepts ReadOnlyMemory<byte>.
        await blob.UploadAsync(BinaryData.FromBytes(payload), options, cancellationToken)
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

        var blob = _containerClient.GetBlobClient(token.Id);
        var response = await blob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
        return response.Value.Content.ToMemory();
    }

    public async Task DeleteAsync(
        ClaimCheckToken token,
        CancellationToken cancellationToken = default)
    {
        if (token is null)
        {
            throw new ArgumentNullException(nameof(token));
        }

        var blob = _containerClient.GetBlobClient(token.Id);
        await blob.DeleteIfExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
