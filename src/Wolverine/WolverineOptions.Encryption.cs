using System.Collections.Concurrent;
using Wolverine.Runtime.Serialization.Encryption;

namespace Wolverine;

public sealed partial class WolverineOptions
{
    /// <summary>
    /// Encrypt all outgoing messages by default with AES-256-GCM. The current
    /// <see cref="DefaultSerializer"/> is wrapped as the inner serializer and
    /// remains resolvable under its original content-type so receive-side
    /// dispatch can decrypt and dispatch back through it.
    /// </summary>
    /// <param name="keyProvider">Required key provider. Must not be null.</param>
    public void UseEncryption(IKeyProvider keyProvider)
    {
        if (keyProvider is null) throw new ArgumentNullException(nameof(keyProvider));

        // Calling UseEncryption twice would wrap an already-wrapping serializer:
        // outgoing messages would be encrypted twice, but the receive-side path
        // only unwraps one layer, so messages would silently be undecryptable.
        if (DefaultSerializer is EncryptingMessageSerializer)
        {
            throw new InvalidOperationException(
                "UseEncryption has already been called on this WolverineOptions. " +
                "Calling it more than once would double-wrap the default serializer " +
                "and produce envelopes that cannot be decrypted on receive. " +
                "Configure encryption exactly once during host setup.");
        }

        var inner = DefaultSerializer;
        var encrypting = new EncryptingMessageSerializer(inner, keyProvider);

        // The setter below also registers the encrypting serializer under its
        // own content-type via _serializers; the inner serializer remains in
        // _serializers under its original content-type for receive-side use.
        DefaultSerializer = encrypting;
    }

    /// <summary>
    /// Register the encrypting serializer alongside the existing default serializer
    /// without changing the default. Use this when you want per-message-type or
    /// per-endpoint opt-in encryption while leaving non-opted-in messages serialized
    /// normally.
    /// </summary>
    /// <param name="keyProvider">Required key provider. Must not be null.</param>
    public void RegisterEncryptionSerializer(IKeyProvider keyProvider)
    {
        if (keyProvider is null) throw new ArgumentNullException(nameof(keyProvider));

        // Same hazard as UseEncryption: a second registration replaces the first
        // under the same content-type but the inner-of-inner reference would now
        // point at the previous EncryptingMessageSerializer, double-wrapping every
        // future per-type / per-endpoint encryption.
        if (TryFindSerializer(EncryptionHeaders.EncryptedContentType) is not null)
        {
            throw new InvalidOperationException(
                "An encrypting serializer is already registered on this WolverineOptions. " +
                "Configure encryption exactly once during host setup.");
        }

        var inner = DefaultSerializer;
        var encrypting = new EncryptingMessageSerializer(inner, keyProvider);

        AddSerializer(encrypting);
    }

    private readonly ConcurrentDictionary<Type, bool> _encryptionRequiredCache = new();

    /// <summary>
    /// Message types whose envelopes MUST arrive encrypted. Populated by
    /// <see cref="MessageTypePolicies{T}.Encrypt"/>. Read by
    /// <see cref="IsEncryptionRequired"/> on every inbound envelope; intended
    /// to be populated at setup time only (before <c>host.StartAsync()</c>).
    /// Mutations after startup will not be reflected for types whose answer
    /// has already been cached.
    /// </summary>
    public HashSet<Type> RequiredEncryptedTypes { get; } = new();

    /// <summary>
    /// Listener endpoint URIs that MUST receive only encrypted envelopes.
    /// Populated by the <c>RequireEncryption()</c> method on listener configurations.
    /// Inbound envelopes whose <see cref="Envelope.Destination"/> is in this set
    /// and whose content-type is not the encrypted content-type are routed to the
    /// dead-letter queue with <see cref="EncryptionPolicyViolationException"/>.
    /// </summary>
    public HashSet<Uri> RequiredEncryptedListenerUris { get; } = new();

    /// <summary>
    /// Returns true if envelopes carrying a message of <paramref name="messageType"/>
    /// must arrive encrypted. Performs an exact match against
    /// <see cref="RequiredEncryptedTypes"/> first; on miss, scans for any registered
    /// required type that is assignable from <paramref name="messageType"/>
    /// (mirrors the polymorphic send-side rule in EncryptMessageTypeRule&lt;T&gt;).
    /// Per-type result is cached for O(1) lookup on subsequent envelopes.
    /// </summary>
    public bool IsEncryptionRequired(Type messageType)
    {
        if (messageType is null) return false;

        // Cache lookup first so previously computed answers survive any later
        // mutation of RequiredEncryptedTypes (the documented contract). Cheap
        // when the cache is empty (no-encryption-configured deployments).
        if (_encryptionRequiredCache.TryGetValue(messageType, out var cached)) return cached;

        // No cached answer. If no markers are configured, return false without
        // caching — avoids unbounded cache growth on no-encryption hosts.
        if (RequiredEncryptedTypes.Count == 0) return false;

        return _encryptionRequiredCache.GetOrAdd(messageType, static (mt, set) =>
        {
            if (set.Contains(mt)) return true;
            foreach (var required in set)
            {
                if (required.IsAssignableFrom(mt)) return true;
            }
            return false;
        }, RequiredEncryptedTypes);
    }
}
