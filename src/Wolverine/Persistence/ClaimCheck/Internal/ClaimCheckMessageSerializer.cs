using Wolverine.Runtime.Serialization;

namespace Wolverine.Persistence.ClaimCheck.Internal;

/// <summary>
/// Decorates an inner <see cref="IMessageSerializer"/> with the Wolverine claim-check
/// pipeline. On write, blob-marked properties are pushed to <see cref="IClaimCheckStore"/>
/// and replaced with header tokens. On read, the tokens are resolved back into property values.
/// </summary>
/// <remarks>
/// When the inner serializer implements <see cref="IAsyncMessageSerializer"/> the async paths
/// preserve full asynchrony. The synchronous Write / ReadFromData paths must
/// call into the (async) <see cref="IClaimCheckStore"/> via
/// <c>GetAwaiter().GetResult()</c>; consider configuring an async-friendly transport or
/// pre-uploading payloads outside the serializer if blocking calls are unacceptable.
/// </remarks>
internal sealed class ClaimCheckMessageSerializer : IMessageSerializer, IAsyncMessageSerializer
{
    private readonly IMessageSerializer _inner;
    private readonly IAsyncMessageSerializer? _innerAsync;
    private readonly ClaimCheckStoreRouter _router;

    /// <summary>
    /// Single-store convenience constructor. Wraps <paramref name="store"/> in a router with no routes,
    /// so every message uses the one store (and optional whole-body threshold).
    /// </summary>
    public ClaimCheckMessageSerializer(IMessageSerializer inner, IClaimCheckStore store,
        long? autoOffloadThreshold = null)
        : this(inner, new ClaimCheckStoreRouter(store, autoOffloadThreshold, [],
            new Dictionary<string, IClaimCheckStore>()))
    {
    }

    public ClaimCheckMessageSerializer(IMessageSerializer inner, ClaimCheckStoreRouter router)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _innerAsync = inner as IAsyncMessageSerializer;
    }

    public IMessageSerializer Inner => _inner;

    /// <summary>The global default store — the one used when no route matches. See GH-3508.</summary>
    public IClaimCheckStore Store => _router.DefaultStore;

    /// <summary>
    /// Global default size (in bytes) at which the whole serialized body is auto-offloaded even when no
    /// <see cref="BlobAttribute"/> is present. Null disables the safety net. Per-route overrides are
    /// applied by the router. See GH-3504.
    /// </summary>
    public long? AutoOffloadThreshold => _router.DefaultThreshold;

    public string ContentType => _inner.ContentType;

    public byte[] Write(Envelope envelope)
    {
        var message = envelope.Message;
        var selection = _router.ResolveForSend(message?.GetType(), envelope);
        // The list is owned by the caller and lives outside the try so that a partial off-load —
        // StoreBlobsAsync throwing after it has already cleared one or more properties — is still
        // visible to the finally and gets restored. Otherwise a failed off-load would leak cleared
        // properties into subsequent in-process handling of the same Envelope.
        var offloaded = new List<OffloadedBlob>();
        try
        {
            if (message is not null)
            {
                var info = BlobTypeInfo.For(message.GetType());
                if (info.HasBlobs)
                {
#pragma warning disable VSTHRD002 // Documented blocking call, see class remarks
                    StoreBlobsAsync(envelope, message, info, offloaded, selection).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
                }
            }

#pragma warning disable VSTHRD002 // Documented blocking call, see class remarks
            return maybeOffloadBodyAsync(envelope, _inner.Write(envelope), selection).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
        }
        finally
        {
            RestoreBlobs(message, offloaded);
        }
    }

    public byte[] WriteMessage(object message)
    {
        var offloaded = new List<OffloadedBlob>();
        try
        {
            if (message is not null)
            {
                var info = BlobTypeInfo.For(message.GetType());
                if (info.HasBlobs)
                {
                    // No envelope is available here so the tokens cannot be smuggled
                    // back to the consumer. We still upload the payloads for symmetry,
                    // but nullify the properties so the inner serializer doesn't pull
                    // bytes through the wire under both paths.
                    var selection = _router.ResolveForSend(message.GetType(), envelope: null);
#pragma warning disable VSTHRD002
                    StoreBlobsAsync(envelope: null, message, info, offloaded, selection).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
                }
            }

            return _inner.WriteMessage(message!);
        }
        finally
        {
            RestoreBlobs(message, offloaded);
        }
    }

    public async ValueTask<byte[]> WriteAsync(Envelope envelope)
    {
        var message = envelope.Message;
        var selection = _router.ResolveForSend(message?.GetType(), envelope);
        var offloaded = new List<OffloadedBlob>();
        try
        {
            if (message is not null)
            {
                var info = BlobTypeInfo.For(message.GetType());
                if (info.HasBlobs)
                {
                    await StoreBlobsAsync(envelope, message, info, offloaded, selection).ConfigureAwait(false);
                }
            }

            var body = _innerAsync is not null
                ? await _innerAsync.WriteAsync(envelope).ConfigureAwait(false)
                : _inner.Write(envelope);

            return await maybeOffloadBodyAsync(envelope, body, selection).ConfigureAwait(false);
        }
        finally
        {
            RestoreBlobs(message, offloaded);
        }
    }

    /// <summary>
    /// GH-3504 whole-body safety net: when the serialized body exceeds the configured threshold,
    /// off-load the entire body to the store and carry a single reference header, returning an empty
    /// wire body. Runs after per-property <see cref="BlobAttribute"/> off-loading, so it measures the
    /// actual body that would otherwise go on the wire. A null threshold or a body already under it is
    /// returned untouched. Without an envelope there is nowhere to smuggle the reference header, so the
    /// body is left as-is (e.g. the WriteMessage(object) path).
    /// </summary>
    private async Task<byte[]> maybeOffloadBodyAsync(Envelope? envelope, byte[] body, ClaimCheckSelection selection)
    {
        if (envelope is null || !selection.Threshold.HasValue || body.LongLength <= selection.Threshold.Value)
        {
            return body;
        }

        var token = await selection.Store.StoreAsync(body, _inner.ContentType).ConfigureAwait(false);
        envelope.Headers[ClaimCheckHeaders.BodyHeaderName] = token.Serialize();
        stampStoreKey(envelope, selection);
        return [];
    }

    /// <summary>
    /// Record which store this envelope's claim-check payloads were off-loaded to (GH-3508), but only
    /// when a non-default route was selected — default-store envelopes carry no store-key header and stay
    /// identical to pre-routing behavior.
    /// </summary>
    private static void stampStoreKey(Envelope envelope, ClaimCheckSelection selection)
    {
        if (selection.StoreKey is not null)
        {
            envelope.Headers[ClaimCheckHeaders.StoreHeaderName] = selection.StoreKey;
        }
    }

    /// <summary>
    /// GH-3504 receive side: if the whole body was auto-offloaded (a body reference header is present),
    /// pull the real bytes back from <paramref name="store"/> into <see cref="Envelope.Data"/> before the
    /// inner serializer deserializes it. A no-op when the header is absent. Runs before per-property
    /// re-hydration, which reads its own tokens from the restored body's headers.
    /// </summary>
    private static async Task restoreOffloadedBodyAsync(Envelope envelope, IClaimCheckStore store)
    {
        if (!envelope.TryGetHeader(ClaimCheckHeaders.BodyHeaderName, out var headerValue))
        {
            return;
        }

        if (!ClaimCheckToken.TryParse(headerValue, out var token))
        {
            throw new FormatException(
                $"Header '{ClaimCheckHeaders.BodyHeaderName}' on envelope {envelope.Id} is not a valid claim-check token: '{headerValue}'.");
        }

        var bytes = await store.LoadAsync(token).ConfigureAwait(false);
        envelope.Data = bytes.ToArray();
    }

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        var store = _router.ResolveForReceive(envelope);
#pragma warning disable VSTHRD002
        restoreOffloadedBodyAsync(envelope, store).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
        var message = _inner.ReadFromData(messageType, envelope);
        if (message is not null)
        {
            var info = BlobTypeInfo.For(message.GetType());
            if (info.HasBlobs)
            {
#pragma warning disable VSTHRD002
                LoadBlobsAsync(envelope, message, info, store).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            }
        }

        return message!;
    }

    public object ReadFromData(byte[] data)
    {
        // No envelope context, so claim-check rehydration is not possible here.
        return _inner.ReadFromData(data);
    }

    public async ValueTask<object?> ReadFromDataAsync(Type messageType, Envelope envelope)
    {
        var store = _router.ResolveForReceive(envelope);
        await restoreOffloadedBodyAsync(envelope, store).ConfigureAwait(false);

        object? message;
        if (_innerAsync is not null)
        {
            message = await _innerAsync.ReadFromDataAsync(messageType, envelope).ConfigureAwait(false);
        }
        else
        {
            message = _inner.ReadFromData(messageType, envelope);
        }

        if (message is not null)
        {
            var info = BlobTypeInfo.For(message.GetType());
            if (info.HasBlobs)
            {
                await LoadBlobsAsync(envelope, message, info, store).ConfigureAwait(false);
            }
        }

        return message;
    }

    /// <summary>
    /// Off-load each blob property to the store and stamp the claim-check header. The property is
    /// nulled on the message only so the inner serializer does not pull the payload bytes into the
    /// persisted/wire body; the original payload is appended to <paramref name="offloaded"/> as soon
    /// as it is cleared so the caller can restore the live message after serialization. This mirrors
    /// <c>EncryptingMessageSerializer</c>, which never leaves the in-memory message mutated — a local
    /// (in-process) hand-off reuses the same object and skips deserialization, so a leaked Clear
    /// would reach the handler as a null property (GH claim-check local-queue re-hydration bug).
    ///
    /// <paramref name="offloaded"/> is owned by the caller and recorded per-property *before* the
    /// next store call, so if this method throws partway through (e.g. the store rejects the second
    /// of two blobs) the caller's finally still restores every property cleared so far.
    /// </summary>
    private async Task StoreBlobsAsync(Envelope? envelope, object message, BlobTypeInfo info,
        List<OffloadedBlob> offloaded, ClaimCheckSelection selection)
    {
        foreach (var accessor in info.Properties)
        {
            var bytes = accessor.ReadPayload(message);
            if (bytes is null || bytes.Length == 0)
            {
                continue;
            }

            var token = await selection.Store.StoreAsync(bytes, accessor.ContentType).ConfigureAwait(false);
            if (envelope is not null)
            {
                envelope.Headers[accessor.HeaderName] = token.Serialize();
                stampStoreKey(envelope, selection);
            }
            accessor.Clear(message);
            offloaded.Add(new OffloadedBlob(accessor, bytes));
        }
    }

    /// <summary>
    /// Re-apply the off-loaded payloads to the live in-memory message after the inner serializer
    /// has produced the body (or after an off-load failure). <see cref="BlobPropertyAccessor.ApplyLoaded"/>
    /// is the same routine the receive path uses to re-hydrate from the store, so a restored Stream
    /// property comes back as a fresh readable stream rather than the consumed original.
    /// </summary>
    private static void RestoreBlobs(object? message, List<OffloadedBlob> offloaded)
    {
        if (message is null)
        {
            return;
        }

        foreach (var blob in offloaded)
        {
            blob.Accessor.ApplyLoaded(message, blob.Payload);
        }
    }

    private readonly record struct OffloadedBlob(BlobPropertyAccessor Accessor, ReadOnlyMemory<byte> Payload);

    private static async Task LoadBlobsAsync(Envelope envelope, object message, BlobTypeInfo info,
        IClaimCheckStore store)
    {
        foreach (var accessor in info.Properties)
        {
            if (!envelope.TryGetHeader(accessor.HeaderName, out var headerValue))
            {
                continue;
            }

            if (!ClaimCheckToken.TryParse(headerValue, out var token))
            {
                throw new FormatException(
                    $"Header '{accessor.HeaderName}' on envelope {envelope.Id} is not a valid claim-check token: '{headerValue}'.");
            }

            var bytes = await store.LoadAsync(token).ConfigureAwait(false);
            accessor.ApplyLoaded(message, bytes);
        }
    }
}
