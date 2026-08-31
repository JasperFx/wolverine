using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using JasperFx.Core.Reflection;

namespace Wolverine.AzureBlobStorage.Internals;

/// <summary>
/// Public because Wolverine generates code that constructs it directly; an internal type would force
/// service location, which ServiceLocationPolicy.NotAllowed refuses.
/// </summary>
public class BlobDocumentSession : IBlobDocumentSession
{
    private static readonly string _blobNotFound = BlobErrorCode.BlobNotFound.ToString();
    private static readonly string _blobAlreadyExists = BlobErrorCode.BlobAlreadyExists.ToString();

    private readonly BlobServiceClient _client;
    private readonly AzureBlobStorageConfiguration _configuration;

    // GH-4160. The ETag of every saga blob this session read, so the matching write can be a
    // compare-and-swap. Generated code builds one session per handler invocation, so the lifetime of
    // this is exactly one message -- load, mutate, save -- which is the window a saga needs guarded.
    private readonly Dictionary<string, ETag> _etags = new();

    public BlobDocumentSession(BlobServiceClient client, AzureBlobStorageConfiguration configuration)
    {
        _client = client;
        _configuration = configuration;
    }

    public async Task<T?> LoadAsync<T>(object id, string? tenantId, CancellationToken token = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(id);

        var mapping = _configuration.MappingFor(typeof(T));
        var name = mapping.BlobNameForIdentity(id, tenantId);

        try
        {
            var response = await blobFor(mapping, name).DownloadContentAsync(token).ConfigureAwait(false);

            if (mapping.IsSaga)
            {
                _etags[cacheKey(mapping, name)] = response.Value.Details.ETag;
            }

            return mapping.Serializer.Deserialize<T>(response.Value.Content.ToMemory());
        }
        catch (RequestFailedException e) when (e.ErrorCode == _blobNotFound)
        {
            // Only a missing BLOB. A 403 on a missing permission has to keep propagating, or a
            // misconfigured container looks exactly like an empty one -- and so does ContainerNotFound,
            // which is also a 404: a mistyped container name would otherwise read as a document that is
            // permanently missing rather than as the configuration error it is.
            return null;
        }
    }

    public Task StoreAsync<T>(T document, string? tenantId, CancellationToken token = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(document);

        var mapping = _configuration.MappingFor(typeof(T));

        return uploadAsync(mapping, mapping.BlobNameForEntity(document, tenantId), document, token);
    }

    public Task DeleteAsync<T>(T document, string? tenantId, CancellationToken token = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(document);

        var mapping = _configuration.MappingFor(typeof(T));

        return deleteAsync(mapping, mapping.BlobNameForEntity(document, tenantId), token);
    }

    public Task DeleteByIdAsync<T>(object id, string? tenantId, CancellationToken token = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(id);

        var mapping = _configuration.MappingFor(typeof(T));

        return deleteAsync(mapping, mapping.BlobNameForIdentity(id, tenantId), token);
    }

    private async Task uploadAsync<T>(BlobDocumentMapping mapping, string name, T document, CancellationToken token)
        where T : class
    {
        var body = mapping.Serializer.Serialize(document);

        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders
            {
                ContentType = mapping.Serializer.ContentType,
                ContentEncoding = mapping.Serializer.ContentEncoding
            }
        };

        // A saga is a read-modify-write, so its upload is a compare-and-swap: If-Match against the ETag
        // this session read, or If-None-Match: * when it read nothing and believes it is creating one.
        // An ordinary document stays last-write-wins -- an unconditional upload overwrites, Blob Storage
        // has no insert-versus-update, and callers are told so. See GH-4160.
        if (mapping.IsSaga)
        {
            options.Conditions = _etags.TryGetValue(cacheKey(mapping, name), out var etag)
                ? new BlobRequestConditions { IfMatch = etag }
                : new BlobRequestConditions { IfNoneMatch = ETag.All };
        }

        try
        {
            var response = await blobFor(mapping, name)
                .UploadAsync(BinaryData.FromBytes(body), options, token).ConfigureAwait(false);

            // Keep the session's view current: a handler that writes the same saga twice in one message
            // must compare against what it just wrote, not against what it originally read.
            if (mapping.IsSaga)
            {
                _etags[cacheKey(mapping, name)] = response.Value.ETag;
            }
        }
        catch (RequestFailedException e) when (mapping.IsSaga && isConcurrencyFailure(e))
        {
            throw new SagaConcurrencyException(
                $"Saga of type {mapping.EntityType.FullNameInCode()} at {mapping.ContainerName}/{name} was changed by another message since it was read",
                e);
        }
    }

    private Task deleteAsync(BlobDocumentMapping mapping, string name, CancellationToken token)
    {
        // DeleteIfExists reports success for a blob that was never there, so this is naturally idempotent.
        return blobFor(mapping, name).DeleteIfExistsAsync(cancellationToken: token);
    }

    private BlobClient blobFor(BlobDocumentMapping mapping, string name)
    {
        return _client.GetBlobContainerClient(mapping.ContainerName).GetBlobClient(name);
    }

    // A blob name is only unique within its container, and one session can span containers.
    private static string cacheKey(BlobDocumentMapping mapping, string name) => $"{mapping.ContainerName}/{name}";

    /// <summary>
    /// Blob Storage answers a failed conditional upload with two different statuses, unlike S3's single
    /// 412: a failed <c>If-None-Match: *</c> is <c>409 BlobAlreadyExists</c>, while a failed
    /// <c>If-Match</c> — stale, or against a blob another message has since deleted — is
    /// <c>412 ConditionNotMet</c>. Both mean the same thing here, so both are translated. Verified
    /// against Azurite before the design was written; see <c>azurite_honors_conditional_writes</c>.
    /// </summary>
    private static bool isConcurrencyFailure(RequestFailedException e)
    {
        return e.Status == 412 || (e.Status == 409 && e.ErrorCode == _blobAlreadyExists);
    }
}
