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
    private readonly IClaimCheckStore _store;

    public ClaimCheckMessageSerializer(IMessageSerializer inner, IClaimCheckStore store)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _innerAsync = inner as IAsyncMessageSerializer;
    }

    public IMessageSerializer Inner => _inner;
    public IClaimCheckStore Store => _store;

    public string ContentType => _inner.ContentType;

    public byte[] Write(Envelope envelope)
    {
        var message = envelope.Message;
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
                    StoreBlobsAsync(envelope, message, info, offloaded).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
                }
            }

            return _inner.Write(envelope);
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
#pragma warning disable VSTHRD002
                    StoreBlobsAsync(envelope: null, message, info, offloaded).GetAwaiter().GetResult();
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
        var offloaded = new List<OffloadedBlob>();
        try
        {
            if (message is not null)
            {
                var info = BlobTypeInfo.For(message.GetType());
                if (info.HasBlobs)
                {
                    await StoreBlobsAsync(envelope, message, info, offloaded).ConfigureAwait(false);
                }
            }

            if (_innerAsync is not null)
            {
                return await _innerAsync.WriteAsync(envelope).ConfigureAwait(false);
            }

            return _inner.Write(envelope);
        }
        finally
        {
            RestoreBlobs(message, offloaded);
        }
    }

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        var message = _inner.ReadFromData(messageType, envelope);
        if (message is not null)
        {
            var info = BlobTypeInfo.For(message.GetType());
            if (info.HasBlobs)
            {
#pragma warning disable VSTHRD002
                LoadBlobsAsync(envelope, message, info).GetAwaiter().GetResult();
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
                await LoadBlobsAsync(envelope, message, info).ConfigureAwait(false);
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
        List<OffloadedBlob> offloaded)
    {
        foreach (var accessor in info.Properties)
        {
            var bytes = accessor.ReadPayload(message);
            if (bytes is null || bytes.Length == 0)
            {
                continue;
            }

            var token = await _store.StoreAsync(bytes, accessor.ContentType).ConfigureAwait(false);
            if (envelope is not null)
            {
                envelope.Headers[accessor.HeaderName] = token.Serialize();
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

    private async Task LoadBlobsAsync(Envelope envelope, object message, BlobTypeInfo info)
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

            var bytes = await _store.LoadAsync(token).ConfigureAwait(false);
            accessor.ApplyLoaded(message, bytes);
        }
    }
}
