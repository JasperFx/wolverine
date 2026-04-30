namespace Wolverine.Runtime.Serialization.Encryption;

public abstract class MessageEncryptionException : Exception
{
    public string KeyId { get; }

    protected MessageEncryptionException(string keyId, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        KeyId = keyId;
    }
}
