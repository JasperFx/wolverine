namespace Wolverine.Runtime.Serialization.Encryption;

public abstract class MessageEncryptionException : Exception
{
    protected MessageEncryptionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
