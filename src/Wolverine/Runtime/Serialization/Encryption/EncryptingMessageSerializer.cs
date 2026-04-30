using System.Security.Cryptography;

namespace Wolverine.Runtime.Serialization.Encryption;

/// <summary>
/// Decorates an inner <see cref="IMessageSerializer"/> with AES-256-GCM body
/// encryption. Emits dedicated content-type
/// <c>application/wolverine-encrypted+json</c>; receive-side dispatch is
/// content-type driven and lands here, then unwraps to the inner serializer
/// after decryption.
/// </summary>
/// <remarks>
/// When the inner serializer implements <see cref="IAsyncMessageSerializer"/>
/// the async paths preserve full asynchrony. The synchronous Write /
/// ReadFromData(envelope) paths must call into the (async) <see cref="IKeyProvider"/>
/// via <c>GetAwaiter().GetResult()</c>; Wolverine's runtime exercises sync call
/// sites at <c>Envelope.cs</c> (Data property getter) and <c>BatchedSender</c>,
/// so blocking is unavoidable on those paths. Most production paths use the
/// async surface and never block.
///
/// <para><c>WriteMessage(object)</c> and <c>ReadFromData(byte[])</c> have no
/// envelope, so the key-id header cannot be read or written — these paths
/// delegate to the inner serializer without encryption and are not used by
/// Wolverine's normal pipeline.</para>
/// </remarks>
public sealed class EncryptingMessageSerializer : IAsyncMessageSerializer
{
    private readonly IMessageSerializer _inner;
    private readonly IAsyncMessageSerializer? _innerAsync;
    private readonly IKeyProvider _keyProvider;

    public EncryptingMessageSerializer(IMessageSerializer inner, IKeyProvider keyProvider)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _innerAsync = inner as IAsyncMessageSerializer;
    }

    public IMessageSerializer Inner => _inner;
    public string ContentType => EncryptionHeaders.EncryptedContentType;

    public byte[] Write(Envelope envelope)
    {
#pragma warning disable VSTHRD002 // Documented blocking call, see class remarks
        return WriteAsync(envelope).AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
    }

    public byte[] WriteMessage(object message)
    {
        // No envelope is available here, so we cannot resolve a key-id from headers
        // and we cannot mutate envelope headers to record the key used. Delegate to
        // the inner serializer raw — encryption is intentionally a no-op on this path.
        // Wolverine's normal write paths go through Write/WriteAsync(envelope), which
        // are encrypted as expected.
        return _inner.WriteMessage(message);
    }

    public object ReadFromData(Type messageType, Envelope envelope)
    {
#pragma warning disable VSTHRD002
        return ReadFromDataAsync(messageType, envelope).AsTask().GetAwaiter().GetResult()!;
#pragma warning restore VSTHRD002
    }

    public object ReadFromData(byte[] data)
    {
        // No envelope context, so the key-id header is unavailable and decryption
        // cannot be performed. Delegate raw to the inner serializer; this path is
        // not used by Wolverine's normal receive pipeline (HandlerPipeline.cs always
        // has an envelope).
        return _inner.ReadFromData(data);
    }

    public async ValueTask<byte[]> WriteAsync(Envelope envelope)
    {
        var keyId = _keyProvider.DefaultKeyId;
        byte[] key;
        try
        {
            key = await _keyProvider.GetKeyAsync(keyId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new EncryptionKeyNotFoundException(keyId, ex);
        }

        var plaintext = _innerAsync is not null
            ? await _innerAsync.WriteAsync(envelope).ConfigureAwait(false)
            : _inner.Write(envelope);

        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key, tagSizeInBytes: 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        envelope.Headers[EncryptionHeaders.KeyIdHeader] = keyId;
        envelope.Headers[EncryptionHeaders.InnerContentTypeHeader] = _inner.ContentType;

        var output = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce,      0, output, 0,                                    nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, output, nonce.Length,                         ciphertext.Length);
        Buffer.BlockCopy(tag,        0, output, nonce.Length + ciphertext.Length,     tag.Length);
        return output;
    }

    public async ValueTask<object?> ReadFromDataAsync(Type messageType, Envelope envelope)
    {
        if (!envelope.Headers.TryGetValue(EncryptionHeaders.KeyIdHeader, out var keyId)
            || string.IsNullOrEmpty(keyId))
        {
            throw new EncryptionKeyNotFoundException(
                keyId: "<missing>",
                innerException: new InvalidOperationException(
                    $"Envelope is missing required header '{EncryptionHeaders.KeyIdHeader}'."));
        }

        byte[] key;
        try
        {
            key = await _keyProvider.GetKeyAsync(keyId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new EncryptionKeyNotFoundException(keyId, ex);
        }

        var body = envelope.Data ?? Array.Empty<byte>();
        if (body.Length < 12 + 16)
        {
            throw new MessageDecryptionException(keyId,
                new CryptographicException(
                    $"Encrypted body too short ({body.Length} bytes); expected at least 28 (12-byte nonce + 16-byte tag)."));
        }

        var nonce      = body.AsSpan(0,                       12).ToArray();
        var tag        = body.AsSpan(body.Length - 16,        16).ToArray();
        var ciphertext = body.AsSpan(12,                      body.Length - 12 - 16).ToArray();
        var plaintext  = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(key, tagSizeInBytes: 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new MessageDecryptionException(keyId, ex);
        }

        // Restore plaintext to a synthetic envelope view that the inner serializer expects.
        var innerEnvelope = new Envelope
        {
            Data        = plaintext,
            ContentType = envelope.Headers.TryGetValue(EncryptionHeaders.InnerContentTypeHeader, out var innerCt)
                              ? innerCt
                              : _inner.ContentType,
            MessageType = envelope.MessageType,
            Headers     = new Dictionary<string, string?>(envelope.Headers)
        };

        return _innerAsync is not null
            ? await _innerAsync.ReadFromDataAsync(messageType, innerEnvelope).ConfigureAwait(false)
            : _inner.ReadFromData(messageType, innerEnvelope);
    }
}
