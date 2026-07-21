using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.ObjectStore;
using NATS.Net;
using Wolverine.Persistence;

namespace Wolverine.ClaimCheck.Nats;

/// <summary>
/// NATS JetStream Object Store backed <see cref="IClaimCheckStore"/>. Each claim check
/// payload is stored as a single object in the configured object-store bucket. The
/// <see cref="ClaimCheckToken.Id"/> maps directly to the object name; the content type
/// travels with the token, so it does not need to be persisted alongside the bytes.
/// </summary>
public class NatsObjectStoreClaimCheckStore : IClaimCheckStore
{
    private readonly INatsObjContext _context;
    private readonly string _bucketName;

    // The bucket is resolved (created-or-fetched) lazily on first use and cached. Guarded by a
    // gate so concurrent StoreAsync/LoadAsync calls don't race on the create-or-get.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private INatsObjStore? _store;

    /// <summary>
    /// Create a new claim check store backed by a NATS JetStream Object Store bucket, reusing
    /// an existing NATS connection.
    /// </summary>
    /// <param name="connection">A connected <see cref="INatsConnection"/> (JetStream must be enabled on the server).</param>
    /// <param name="bucketName">Name of the object-store bucket used to hold claim check payloads. Created on first use if it does not exist.</param>
    public NatsObjectStoreClaimCheckStore(INatsConnection connection, string bucketName)
        : this(new NatsObjContext(connection.CreateJetStreamContext()), bucketName)
    {
    }

    /// <summary>
    /// Create a new claim check store backed by a NATS JetStream Object Store bucket, using an
    /// already-constructed object-store context.
    /// </summary>
    public NatsObjectStoreClaimCheckStore(INatsObjContext context, string bucketName)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        if (string.IsNullOrWhiteSpace(bucketName))
        {
            throw new ArgumentException("Bucket name must be provided", nameof(bucketName));
        }

        _bucketName = bucketName;
    }

    /// <summary>The configured object-store bucket name.</summary>
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
        var store = await resolveStoreAsync(cancellationToken).ConfigureAwait(false);

        // The convenience PutAsync overload takes a byte[]; ReadOnlyMemory<byte> may be backed by
        // memory that doesn't expose an array, so materialize a stable copy.
        await store.PutAsync(id, payload.ToArray(), cancellationToken).ConfigureAwait(false);

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

        var store = await resolveStoreAsync(cancellationToken).ConfigureAwait(false);
        var bytes = await store.GetBytesAsync(token.Id, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    public async Task DeleteAsync(ClaimCheckToken token, CancellationToken cancellationToken = default)
    {
        if (token is null)
        {
            throw new ArgumentNullException(nameof(token));
        }

        var store = await resolveStoreAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await store.DeleteAsync(token.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (NatsObjNotFoundException)
        {
            // Best-effort delete — a missing object is not an error.
        }
    }

    private async ValueTask<INatsObjStore> resolveStoreAsync(CancellationToken cancellationToken)
    {
        if (_store is not null)
        {
            return _store;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_store is not null)
            {
                return _store;
            }

            try
            {
                _store = await _context.GetObjectStoreAsync(_bucketName, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is NatsObjNotFoundException or NatsJSApiException)
            {
                // The bucket doesn't exist yet (a missing object-store bucket surfaces as a JetStream
                // "stream not found" error). Create it; if another caller won the race, fall back to get.
                try
                {
                    _store = await _context
                        .CreateObjectStoreAsync(new NatsObjConfig(_bucketName), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (NatsJSApiException)
                {
                    _store = await _context.GetObjectStoreAsync(_bucketName, cancellationToken).ConfigureAwait(false);
                }
            }

            return _store;
        }
        finally
        {
            _gate.Release();
        }
    }
}
