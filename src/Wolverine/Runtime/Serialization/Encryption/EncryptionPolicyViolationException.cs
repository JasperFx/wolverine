namespace Wolverine.Runtime.Serialization.Encryption;

/// <summary>
/// Thrown when an inbound envelope arrives without encryption but the
/// receiving message type or listener has been marked as requiring it.
/// The envelope is routed to the dead-letter queue without invoking any
/// serializer; no body bytes are interpreted.
/// </summary>
public sealed class EncryptionPolicyViolationException : MessageEncryptionException
{
    public EncryptionPolicyViolationException(Envelope envelope)
        : base(BuildMessage(envelope))
    {
    }

    private static string BuildMessage(Envelope envelope)
    {
        return $"Envelope of type '{envelope.MessageType}' arrived with content-type "
             + $"'{envelope.ContentType}' but encryption is required for this type or listener.";
    }
}
