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
    /// Resolve the 32-byte AES-256 key for the given key-id. Implementations
    /// SHOULD throw (e.g. <see cref="KeyNotFoundException"/>) when the key is
    /// not available; the encrypting serializer wraps any thrown exception
    /// into <see cref="EncryptionKeyNotFoundException"/>.
    /// </summary>
    ValueTask<byte[]> GetKeyAsync(string keyId, CancellationToken cancellationToken);
}
