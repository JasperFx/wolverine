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
        if (message is not null)
        {
            var info = BlobTypeInfo.For(message.GetType());
            if (info.HasBlobs)
            {
#pragma warning disable VSTHRD002 // Documented blocking call, see class remarks
                StoreBlobsAsync(envelope, message, info).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            }
        }

        return _inner.Write(envelope);
    }

    public byte[] WriteMessage(object message)
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
                StoreBlobsAsync(envelope: null, message, info).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            }
        }

        return _inner.WriteMessage(message!);
    }

    public async ValueTask<byte[]> WriteAsync(Envelope envelope)
    {
        var message = envelope.Message;
        if (message is not null)
        {
            var info = BlobTypeInfo.For(message.GetType());
            if (info.HasBlobs)
            {
                await StoreBlobsAsync(envelope, message, info).ConfigureAwait(false);
            }
        }

        if (_innerAsync is not null)
        {
            return await _innerAsync.WriteAsync(envelope).ConfigureAwait(false);
        }

        return _inner.Write(envelope);
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

    private async Task StoreBlobsAsync(Envelope? envelope, object message, BlobTypeInfo info)
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
        }
    }

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
