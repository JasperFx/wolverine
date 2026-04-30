namespace Wolverine.Runtime.Serialization.Encryption;

/// <summary>
/// Resolves AES-256 encryption keys by key-id. Implementations may be backed by
/// in-memory dictionaries (tests/samples), local config, or remote KMS providers.
/// Wrap remote providers with <see cref="CachingKeyProvider"/> in production —
/// the encrypting serializer hits this on every send and every receive.
/// </summary>
public interface IKeyProvider
{
    /// <summary>
    /// The key-id that outgoing messages are encrypted under. Static at provider
    /// construction; rotation happens by deploying a new provider with a new default.
    /// </summary>
    string DefaultKeyId { get; }

    /// <summary>
    /// Resolve the 32-byte AES-256 key for the given key-id.
    /// </summary>
    /// <remarks>
    /// <para>The returned array is treated as a <b>borrowed reference</b> owned by the
    /// provider. Callers MUST NOT mutate the returned bytes or call
    /// <c>CryptographicOperations.ZeroMemory</c> on them — doing so will corrupt
    /// providers that cache key material (such as <see cref="InMemoryKeyProvider"/>).
    /// Providers that intend to support caller-side zeroization must return a fresh
    /// copy on every call and document that contract explicitly.</para>
    ///
    /// <para>Implementations SHOULD throw (e.g. <see cref="KeyNotFoundException"/>) when
    /// the key is not available; the encrypting serializer wraps any thrown exception
    /// into <see cref="EncryptionKeyNotFoundException"/>.</para>
    /// </remarks>
    ValueTask<byte[]> GetKeyAsync(string keyId, CancellationToken cancellationToken);
}
