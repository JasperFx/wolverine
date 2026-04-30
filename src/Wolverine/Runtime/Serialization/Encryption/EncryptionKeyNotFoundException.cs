namespace Wolverine.Runtime.Serialization.Encryption;

public sealed class EncryptionKeyNotFoundException : MessageEncryptionException
{
    public EncryptionKeyNotFoundException(string keyId, Exception? innerException = null)
        : base(keyId, $"No encryption key available for key-id '{keyId}'", innerException)
    {
    }
}
