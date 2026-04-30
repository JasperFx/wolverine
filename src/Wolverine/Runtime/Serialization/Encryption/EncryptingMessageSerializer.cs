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

    private const string AadMagic = "wlv-enc-v1";

    private static void EnsureKeyMatchesAes256(string keyId, byte[]? key)
    {
        // The AesGcm constructor would throw CryptographicException for any other length,
        // but at a code site that sits outside the WriteAsync/ReadFromDataAsync try-catch,
        // so it would surface as a raw CryptographicException to user code. Wrap here
        // with the key-id surfaced so misconfigured custom IKeyProvider implementations
        // produce a diagnosable error instead of an opaque crypto exception.
        if (key is null || key.Length != 32)
        {
            throw new EncryptionKeyNotFoundException(
                keyId,
                new InvalidOperationException(
                    $"Key provider returned a key of {key?.Length ?? 0} bytes for key-id '{keyId}'; " +
                    "AES-256-GCM requires exactly 32 bytes."));
        }
    }

    internal static byte[] BuildAad(string? messageType, string keyId, string innerContentType)
    {
        var mtSrc = messageType ?? string.Empty;

        var mtLen  = System.Text.Encoding.UTF8.GetByteCount(mtSrc);
        if (mtLen  > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(messageType));

        var kidLen = System.Text.Encoding.UTF8.GetByteCount(keyId);
        if (kidLen > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(keyId));

        var ictLen = System.Text.Encoding.UTF8.GetByteCount(innerContentType);
        if (ictLen > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(innerContentType));

        var magic = System.Text.Encoding.ASCII.GetBytes(AadMagic);
        var mt    = System.Text.Encoding.UTF8.GetBytes(mtSrc);
        var kid   = System.Text.Encoding.UTF8.GetBytes(keyId);
        var ict   = System.Text.Encoding.UTF8.GetBytes(innerContentType);

        var size = magic.Length + 2 + mt.Length + 2 + kid.Length + 2 + ict.Length;
        var buf  = new byte[size];
        var pos  = 0;

        Buffer.BlockCopy(magic, 0, buf, pos, magic.Length); pos += magic.Length;

        buf[pos++] = (byte)(mt.Length >> 8); buf[pos++] = (byte)(mt.Length & 0xFF);
        Buffer.BlockCopy(mt, 0, buf, pos, mt.Length); pos += mt.Length;

        buf[pos++] = (byte)(kid.Length >> 8); buf[pos++] = (byte)(kid.Length & 0xFF);
        Buffer.BlockCopy(kid, 0, buf, pos, kid.Length); pos += kid.Length;

        buf[pos++] = (byte)(ict.Length >> 8); buf[pos++] = (byte)(ict.Length & 0xFF);
        Buffer.BlockCopy(ict, 0, buf, pos, ict.Length);

        return buf;
    }

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

        EnsureKeyMatchesAes256(keyId, key);

        var plaintext = _innerAsync is not null
            ? await _innerAsync.WriteAsync(envelope).ConfigureAwait(false)
            : _inner.Write(envelope);

        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        var aad = BuildAad(envelope.MessageType, keyId, _inner.ContentType);
        using var aes = new AesGcm(key, tagSizeInBytes: 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);

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

        EnsureKeyMatchesAes256(keyId, key);

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

        var hasInnerCt = envelope.Headers.TryGetValue(EncryptionHeaders.InnerContentTypeHeader, out var innerCt);
        // AAD and the inner-serializer ContentType use deliberately different fallbacks
        // when the header is missing/null: AAD uses empty string (encrypt always writes
        // the header, so empty here means tamper or older sender → tag mismatch is the
        // correct security outcome), inner ContentType falls back to _inner.ContentType
        // for legacy-envelope compatibility on the dispatch path. Do not collapse these.
        var innerCtForAad = hasInnerCt ? innerCt ?? string.Empty : string.Empty;
        var aad = BuildAad(envelope.MessageType, keyId, innerCtForAad);

        try
        {
            using var aes = new AesGcm(key, tagSizeInBytes: 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        }
        catch (CryptographicException ex)
        {
            throw new MessageDecryptionException(keyId, ex);
        }

        // Restore plaintext to a synthetic envelope view that the inner serializer expects.
        var innerEnvelope = new Envelope
        {
            Data        = plaintext,
            ContentType = hasInnerCt ? innerCt : _inner.ContentType,
            MessageType = envelope.MessageType,
            Headers     = new Dictionary<string, string?>(envelope.Headers)
        };

        return _innerAsync is not null
            ? await _innerAsync.ReadFromDataAsync(messageType, innerEnvelope).ConfigureAwait(false)
            : _inner.ReadFromData(messageType, innerEnvelope);
    }
}
