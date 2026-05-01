namespace Wolverine.Runtime.Serialization.Encryption;

public sealed class InMemoryKeyProvider : IKeyProvider
{
    private readonly Dictionary<string, byte[]> _keys;

    public InMemoryKeyProvider(string defaultKeyId, IDictionary<string, byte[]> keys)
    {
        if (string.IsNullOrEmpty(defaultKeyId))
            throw new ArgumentException("defaultKeyId is required", nameof(defaultKeyId));
        if (keys is null) throw new ArgumentNullException(nameof(keys));

        foreach (var (id, bytes) in keys)
        {
            if (bytes is null || bytes.Length != 32)
                throw new ArgumentException(
                    $"Key '{id}' must be exactly 32 bytes for AES-256; got {bytes?.Length ?? 0}.",
                    nameof(keys));
        }

        if (!keys.ContainsKey(defaultKeyId))
            throw new ArgumentException(
                $"defaultKeyId '{defaultKeyId}' is not present in the keys dictionary.",
                nameof(defaultKeyId));

        _keys = new Dictionary<string, byte[]>(keys.Count);
        foreach (var (id, bytes) in keys)
        {
            _keys[id] = bytes.AsSpan().ToArray();
        }

        DefaultKeyId = defaultKeyId;
    }

    public string DefaultKeyId { get; }

    public ValueTask<byte[]> GetKeyAsync(string keyId, CancellationToken cancellationToken)
    {
        if (_keys.TryGetValue(keyId, out var key))
        {
            // The IKeyProvider contract documents the returned array as a borrowed
            // reference that callers MUST NOT mutate. For the in-memory provider
            // the marginal cost (32 bytes per call) is negligible and a defensive
            // copy guarantees that a misbehaving consumer cannot corrupt all
            // subsequent encryptions by writing to the cached key.
            return ValueTask.FromResult(key.AsSpan().ToArray());
        }

        throw new KeyNotFoundException($"No key registered for key-id '{keyId}'.");
    }
}
