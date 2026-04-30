namespace Wolverine.Runtime.Serialization.Encryption;

public static class EncryptionHeaders
{
    public const string EncryptedContentType   = "application/wolverine-encrypted+json";
    public const string KeyIdHeader            = "wolverine.encryption.key-id";
    public const string InnerContentTypeHeader = "wolverine.encryption.inner-content-type";
}
