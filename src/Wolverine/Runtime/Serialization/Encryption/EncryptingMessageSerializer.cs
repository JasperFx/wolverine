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
/// <para>The <see cref="IAsyncMessageSerializer"/> contract has no
/// <see cref="CancellationToken"/> parameter, so calls into
/// <see cref="IKeyProvider.GetKeyAsync"/> from <c>WriteAsync</c> and
/// <c>ReadFromDataAsync</c> use <c>CancellationToken.None</c>. A slow KMS
/// fetch cannot be cancelled by host shutdown; key-provider implementations
/// SHOULD apply their own internal timeouts.</para>
///
/// <para><c>WriteMessage(object)</c> and <c>ReadFromData(byte[])</c> have no
/// envelope context, so the key-id header cannot be read or written. These
/// overloads throw <see cref="InvalidOperationException"/> because returning
/// plaintext on a serializer whose advertised content-type is the encrypted
/// content-type would be a silent confidentiality bug. Wolverine's normal
/// pipeline always carries an envelope and never reaches these overloads.</para>
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

        var size = AadMagic.Length + 2 + mtLen + 2 + kidLen + 2 + ictLen;
        var buf  = new byte[size];
        var span = buf.AsSpan();
        var pos  = 0;

        // ASCII-only magic; GetBytes(string, Span<byte>) writes directly into buf.
        pos += System.Text.Encoding.ASCII.GetBytes(AadMagic, span);

        span[pos++] = (byte)(mtLen >> 8); span[pos++] = (byte)(mtLen & 0xFF);
        pos += System.Text.Encoding.UTF8.GetBytes(mtSrc, span.Slice(pos));

        span[pos++] = (byte)(kidLen >> 8); span[pos++] = (byte)(kidLen & 0xFF);
        pos += System.Text.Encoding.UTF8.GetBytes(keyId, span.Slice(pos));

        span[pos++] = (byte)(ictLen >> 8); span[pos++] = (byte)(ictLen & 0xFF);
        System.Text.Encoding.UTF8.GetBytes(innerContentType, span.Slice(pos));

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
        // No envelope is available, so the key-id header cannot be written. Returning
        // the inner serializer's plaintext on a serializer whose ContentType advertises
        // encryption would be a silent confidentiality bug for any caller who picks
        // this overload by ContentType lookup. Fail loudly instead.
        throw new InvalidOperationException(
            "EncryptingMessageSerializer.WriteMessage(object) cannot encrypt without an Envelope. " +
            "Wolverine's normal write paths use Write(envelope) / WriteAsync(envelope); custom callers " +
            "must pass an Envelope so the key-id header can be stamped.");
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
        // cannot be performed. Wolverine's normal receive pipeline always carries
        // an envelope (HandlerPipeline.TryDeserializeEnvelope). Fail loudly so
        // a stray caller can't silently bypass decryption.
        throw new InvalidOperationException(
            "EncryptingMessageSerializer.ReadFromData(byte[]) cannot decrypt without an Envelope. " +
            "Wolverine's normal receive paths use ReadFromData(messageType, envelope); custom callers " +
            "must pass an Envelope so the key-id header can be read.");
    }

    public async ValueTask<byte[]> WriteAsync(Envelope envelope)
    {
        var keyId = _keyProvider.DefaultKeyId;
        if (string.IsNullOrEmpty(keyId))
        {
            throw new EncryptionKeyNotFoundException(
                keyId: "<null>",
                innerException: new InvalidOperationException(
                    $"Key provider {_keyProvider.GetType().Name} returned a null/empty DefaultKeyId. " +
                    "Implementations must return a stable, non-empty key-id."));
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
        // AAD uses empty string when the header is missing. Wolverine's WriteAsync
        // always writes the header with the inner serializer's content-type, so a
        // missing header here means a non-Wolverine sender or tampering — the tag
        // check below will reject it because the AAD won't match.
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

        // Defense-in-depth: the tag check passed, so AAD matched. If the sender
        // bound an empty inner-content-type into AAD (Wolverine never does this),
        // we have no trustworthy content-type for the inner serializer. Reject.
        if (!hasInnerCt || string.IsNullOrEmpty(innerCt))
        {
            throw new MessageDecryptionException(keyId,
                new CryptographicException(
                    $"Encrypted envelope is missing required header '{EncryptionHeaders.InnerContentTypeHeader}'."));
        }

        // Restore plaintext to a synthetic envelope view that the inner serializer expects.
        var innerEnvelope = new Envelope
        {
            Data        = plaintext,
            ContentType = innerCt,
            MessageType = envelope.MessageType,
            Headers     = new Dictionary<string, string?>(envelope.Headers)
        };

        return _innerAsync is not null
            ? await _innerAsync.ReadFromDataAsync(messageType, innerEnvelope).ConfigureAwait(false)
            : _inner.ReadFromData(messageType, innerEnvelope);
    }
}
