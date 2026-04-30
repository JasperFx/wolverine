namespace Wolverine.Runtime.Serialization.Encryption;

public sealed class MessageDecryptionException : MessageEncryptionException
{
    public MessageDecryptionException(string keyId, Exception innerException)
        : base(keyId, $"Failed to decrypt message body using key-id '{keyId}' (auth tag mismatch or malformed body)", innerException)
    {
    }
}
