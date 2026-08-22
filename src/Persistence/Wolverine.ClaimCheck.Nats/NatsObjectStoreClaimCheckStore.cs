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
public class NatsObjectStoreClaimCheckStore : IClaimCheckStoreWithExpiration
{
    private readonly INatsObjContext _context;
    private readonly string _bucketName;
    private readonly TimeSpan? _maxAge;

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
    /// <param name="maxAge">
    /// Optional native bucket TTL. When Wolverine creates the bucket it is configured with this
    /// <see cref="NatsObjConfig.MaxAge"/>, so the NATS server expires payloads itself — the cheapest
    /// option, and it keeps working while the application is down. See GH-4006.
    /// </param>
    public NatsObjectStoreClaimCheckStore(INatsConnection connection, string bucketName,
        TimeSpan? maxAge = null)
        : this(new NatsObjContext(connection.CreateJetStreamContext()), bucketName, maxAge)
    {
    }

    /// <summary>
    /// Create a new claim check store backed by a NATS JetStream Object Store bucket, using an
    /// already-constructed object-store context.
    /// </summary>
    public NatsObjectStoreClaimCheckStore(INatsObjContext context, string bucketName,
        TimeSpan? maxAge = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));

        if (maxAge is { } age && age <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAge),
                "The object-store bucket max age must be a positive duration.");
        }

        _maxAge = maxAge;
        if (string.IsNullOrWhiteSpace(bucketName))
        {
            throw new ArgumentException("Bucket name must be provided", nameof(bucketName));
        }

        _bucketName = bucketName;
    }

    /// <summary>The configured object-store bucket name.</summary>
    public string BucketName => _bucketName;

    /// <summary>
    /// The native bucket TTL applied when Wolverine creates the bucket, or null when none was configured.
    /// </summary>
    public TimeSpan? MaxAge => _maxAge;

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

    /// <summary>
    /// How long the metadata enumeration will sit quiet before the sweep concludes the bucket is drained.
    /// Only actually waited out when the bucket has no objects at all — see the remarks on
    /// <see cref="DeleteExpiredPayloadsAsync"/>.
    /// </summary>
    private static readonly TimeSpan _drainTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// GH-4006 sweep support. Unlike the S3 / Azure / GCS backends — which deliberately do not implement
    /// this, because enumerating a cloud bucket costs a billed LIST request per pass — a NATS object-store
    /// listing reads the bucket's local metadata stream, so a Wolverine-driven sweep is cheap here.
    /// </summary>
    /// <remarks>
    /// Prefer configuring a native bucket <c>maxAge</c> when Wolverine creates the bucket; that expires
    /// payloads server-side and keeps working while the application is down. This sweep exists for buckets
    /// Wolverine did not create (and therefore cannot configure), and so that
    /// <c>DeletePayloadsOlderThan(...)</c> behaves uniformly across every backend.
    ///
    /// <para>Three behaviours of the NATS client shape this method, all confirmed against a live server:</para>
    /// <list type="bullet">
    /// <item><b>The first <c>OnNoData</c> callback fires before the initial fetch has landed</b>, so
    /// returning <c>true</c> from it aborts the enumeration with zero results even when the bucket is full.
    /// It has to return <c>false</c> once to let the data arrive, and <c>true</c> thereafter.</item>
    /// <item><b>On a genuinely empty bucket <c>OnNoData</c> is never called a second time</b>, so the
    /// enumeration would park forever. The linked idle deadline below is what bounds that case; it is the
    /// only case that actually waits.</item>
    /// <item><b>Breaking out of the enumeration early hangs on disposal.</b> So this always drains the
    /// whole metadata stream and applies <paramref name="maxCount"/> to the delete step instead. That is
    /// cheap — the stream is local metadata, not payload bytes.</item>
    /// </list>
    /// </remarks>
    public async Task<int> DeleteExpiredPayloadsAsync(
        DateTimeOffset cutoff,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        if (maxCount <= 0)
        {
            return 0;
        }

        var store = await resolveStoreAsync(cancellationToken).ConfigureAwait(false);

        var expired = new List<string>();

        using var idle = new CancellationTokenSource(_drainTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idle.Token);

        var noDataCalls = 0;
        var options = new NatsObjListOpts
        {
            OnNoData = _ => new ValueTask<bool>(++noDataCalls > 1)
        };

        try
        {
            await foreach (var metadata in store.ListAsync(options, linked.Token).ConfigureAwait(false))
            {
                // Every delivered item pushes the quiet deadline out, so a large bucket is never cut off
                // mid-scan; the deadline only expires once the stream really has gone silent.
                idle.CancelAfter(_drainTimeout);

                if (metadata.Deleted || metadata.Name is null)
                {
                    // Tombstones stay in the metadata stream after a delete, so they must be filtered or
                    // the sweep would try to delete the same object forever.
                    continue;
                }

                if (metadata.MTime < cutoff && expired.Count < maxCount)
                {
                    expired.Add(metadata.Name);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own idle deadline, not the caller's token: the bucket is drained (or empty). Whatever
            // was collected before it fired is a complete-enough batch — the sweep is idempotent and runs
            // again on the next interval.
        }

        var deleted = 0;
        foreach (var name in expired)
        {
            try
            {
                await store.DeleteAsync(name, cancellationToken).ConfigureAwait(false);
                deleted++;
            }
            catch (NatsObjNotFoundException)
            {
                // Another node's sweeper got there first; not an error.
            }
        }

        return deleted;
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
                    // MaxAge is init-only, so it has to be set in the initializer. It is native,
                    // server-side expiry on the bucket's underlying JetStream stream, and only applies
                    // to a bucket Wolverine creates -- an existing bucket keeps whatever max age it was
                    // already configured with. See GH-4006.
                    var config = _maxAge.HasValue
                        ? new NatsObjConfig(_bucketName) { MaxAge = _maxAge.Value }
                        : new NatsObjConfig(_bucketName);

                    _store = await _context
                        .CreateObjectStoreAsync(config, cancellationToken)
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
