namespace Wolverine.Transports;

/// <summary>
/// Exception thrown when a message is too large to fit in any transport batch.
/// Use this in sending failure policies to discard or dead-letter oversized messages.
/// </summary>
public class MessageTooLargeException : Exception
{
    public MessageTooLargeException(Envelope envelope, long maxSizeInBytes)
        : base($"Message {envelope.Id} of type '{envelope.MessageType}' is too large to fit in a batch (max size: {maxSizeInBytes} bytes)")
    {
        EnvelopeId = envelope.Id;
        MessageType = envelope.MessageType;
        MaxSizeInBytes = maxSizeInBytes;
    }

    public Guid EnvelopeId { get; }
    public string? MessageType { get; }
    public long MaxSizeInBytes { get; }
}
