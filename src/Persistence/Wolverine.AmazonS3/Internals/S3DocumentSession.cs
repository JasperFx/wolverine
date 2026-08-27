using System.Net;
using Amazon.S3;
using Amazon.S3.Model;

namespace Wolverine.AmazonS3.Internals;

/// <summary>
/// Public because Wolverine generates code that constructs it directly; an internal type would force
/// service location, which ServiceLocationPolicy.NotAllowed refuses.
/// </summary>
public class S3DocumentSession : IS3DocumentSession
{
    private readonly IAmazonS3 _client;
    private readonly AmazonS3Configuration _configuration;

    public S3DocumentSession(IAmazonS3 client, AmazonS3Configuration configuration)
    {
        _client = client;
        _configuration = configuration;
    }

    public async Task<T?> LoadAsync<T>(object id, string? tenantId, CancellationToken token = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(id);

        var mapping = _configuration.MappingFor(typeof(T));

        try
        {
            using var response = await _client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = mapping.BucketName,
                Key = mapping.KeyForIdentity(id, tenantId)
            }, token).ConfigureAwait(false);

            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, token).ConfigureAwait(false);

            return mapping.Serializer.Deserialize<T>(buffer.ToArray());
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // Only 404. A 403 on a missing permission has to keep propagating, or a misconfigured
            // bucket looks exactly like an empty one.
            return null;
        }
    }

    public Task StoreAsync<T>(T document, string? tenantId, CancellationToken token = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(document);

        var mapping = _configuration.MappingFor(typeof(T));

        return putAsync(mapping, mapping.KeyForEntity(document, tenantId), document, token);
    }

    public Task DeleteAsync<T>(T document, string? tenantId, CancellationToken token = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(document);

        var mapping = _configuration.MappingFor(typeof(T));

        return deleteAsync(mapping, mapping.KeyForEntity(document, tenantId), token);
    }

    public Task DeleteByIdAsync<T>(object id, string? tenantId, CancellationToken token = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(id);

        var mapping = _configuration.MappingFor(typeof(T));

        return deleteAsync(mapping, mapping.KeyForIdentity(id, tenantId), token);
    }

    private async Task putAsync<T>(S3DocumentMapping mapping, string key, T document, CancellationToken token)
        where T : class
    {
        var body = mapping.Serializer.Serialize(document);
        using var stream = new MemoryStream(body.ToArray(), false);

        var request = new PutObjectRequest
        {
            BucketName = mapping.BucketName,
            Key = key,
            InputStream = stream,
            ContentType = mapping.Serializer.ContentType
        };

        if (mapping.Serializer.ContentEncoding != null)
        {
            request.Headers.ContentEncoding = mapping.Serializer.ContentEncoding;
        }

        await _client.PutObjectAsync(request, token).ConfigureAwait(false);
    }

    private Task deleteAsync(S3DocumentMapping mapping, string key, CancellationToken token)
    {
        // S3 reports success for a key that was never there, so this is naturally idempotent.
        return _client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = mapping.BucketName,
            Key = key
        }, token);
    }
}
