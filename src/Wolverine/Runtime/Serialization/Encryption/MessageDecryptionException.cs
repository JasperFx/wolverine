namespace Wolverine.Runtime.Serialization.Encryption;

public sealed class MessageDecryptionException : MessageEncryptionException
{
    public string KeyId { get; }

    public MessageDecryptionException(string keyId, Exception innerException)
        : base($"Failed to decrypt message body using key-id '{keyId}' (auth tag mismatch or malformed body)", innerException)
    {
        KeyId = keyId;
    }
}
