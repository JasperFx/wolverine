using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace Wolverine.AmazonS3.Internals;

/// <summary>
/// Public because Wolverine generates code that constructs it directly; an internal type would force
/// service location, which ServiceLocationPolicy.NotAllowed refuses.
/// </summary>
public class S3DocumentSession : IS3DocumentSession
{
    private readonly IAmazonS3 _client;
    private readonly AmazonS3Configuration _configuration;

    // GH-4160. The ETag of every saga object this session read, so the matching write can be a
    // compare-and-swap. Generated code builds one session per handler invocation, so the lifetime of
    // this is exactly one message -- load, mutate, save -- which is the window a saga needs guarded.
    private readonly Dictionary<string, string> _etags = new();

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
            var key = mapping.KeyForIdentity(id, tenantId);

            using var response = await _client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = mapping.BucketName,
                Key = key
            }, token).ConfigureAwait(false);

            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, token).ConfigureAwait(false);

            if (mapping.IsSaga && response.ETag.IsNotEmpty())
            {
                _etags[key] = response.ETag;
            }

            return mapping.Serializer.Deserialize<T>(buffer.ToArray());
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound && e.ErrorCode == "NoSuchKey")
        {
            // Only a missing KEY. A 403 on a missing permission has to keep propagating, or a
            // misconfigured bucket looks exactly like an empty one -- and so does NoSuchBucket, which
            // is also a 404: a mistyped bucket name would otherwise read as a document that is
            // permanently missing rather than as the configuration error it is.
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

        // A saga is a read-modify-write, so its put is a compare-and-swap: If-Match against the ETag
        // this session read, or If-None-Match: * when it read nothing and believes it is creating one.
        // An ordinary document stays last-write-wins -- S3 has no insert-versus-update and callers are
        // told so. See GH-4160.
        if (mapping.IsSaga)
        {
            if (_etags.TryGetValue(key, out var etag))
            {
                request.IfMatch = etag;
            }
            else
            {
                request.IfNoneMatch = "*";
            }
        }

        try
        {
            var response = await _client.PutObjectAsync(request, token).ConfigureAwait(false);

            // Keep the session's view current: a handler that writes the same saga twice in one message
            // must compare against what it just wrote, not against what it originally read.
            if (mapping.IsSaga && response.ETag.IsNotEmpty())
            {
                _etags[key] = response.ETag;
            }
        }
        catch (AmazonS3Exception e) when (mapping.IsSaga && e.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new SagaConcurrencyException(
                $"Saga of type {mapping.EntityType.FullNameInCode()} at {mapping.BucketName}/{key} was changed by another message since it was read",
                e);
        }
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
