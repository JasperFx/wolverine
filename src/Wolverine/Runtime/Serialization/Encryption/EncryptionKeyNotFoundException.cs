namespace Wolverine.Runtime.Serialization.Encryption;

public sealed class EncryptionKeyNotFoundException : MessageEncryptionException
{
    public string KeyId { get; }

    public EncryptionKeyNotFoundException(string keyId, Exception? innerException = null)
        : base($"No encryption key available for key-id '{keyId}'", innerException)
    {
        KeyId = keyId;
    }
}
