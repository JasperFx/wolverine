namespace Wolverine.Runtime.Serialization.Encryption;

public static class EncryptionHeaders
{
    public const string EncryptedContentType   = "application/wolverine-encrypted+json";

    /// <summary>
    /// Common prefix shared by all encryption-namespace header keys. Use to
    /// match or filter the entire encryption header set without hard-coding
    /// each key.
    /// </summary>
    public const string HeaderPrefix           = "wolverine.encryption.";

    public const string KeyIdHeader            = "wolverine.encryption.key-id";
    public const string InnerContentTypeHeader = "wolverine.encryption.inner-content-type";
}
