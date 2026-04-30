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

        var inner = DefaultSerializer;
        var encrypting = new EncryptingMessageSerializer(inner, keyProvider);

        AddSerializer(encrypting);
    }
}
