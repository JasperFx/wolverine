using JasperFx.Core;

namespace Wolverine.Transports;

/// <summary>
/// Exception thrown when a message is too large to fit in any transport batch.
/// Use this in sending failure policies to discard or dead-letter oversized messages.
/// </summary>
public class MessageTooLargeException : Exception
{
    public MessageTooLargeException(Envelope envelope, long maxSizeInBytes)
        : this(envelope, maxSizeInBytes, null)
    {
    }

    /// <summary>
    /// GH-4523. The base message gives the limit and stops, which leaves the user with a number and no move
    /// to make. A transport that knows its own remedies -- raise the namespace tier, opt into fragmentation,
    /// use a claim check -- passes them as <paramref name="remedy"/> so the exception says what to do about
    /// it, the way <c>SqsMessageTooLargeException</c> already did.
    /// </summary>
    /// <param name="remedy">
    /// Transport-specific guidance appended to the message. Should also say whether the send is terminal,
    /// since retrying cannot change the size of the message.
    /// </param>
    public MessageTooLargeException(Envelope envelope, long maxSizeInBytes, string? remedy)
        : base(buildMessage(envelope, maxSizeInBytes, remedy))
    {
        EnvelopeId = envelope.Id;
        MessageType = envelope.MessageType;
        MaxSizeInBytes = maxSizeInBytes;
        Remedy = remedy;
    }

    private static string buildMessage(Envelope envelope, long maxSizeInBytes, string? remedy)
    {
        var message =
            $"Message {envelope.Id} of type '{envelope.MessageType}' is too large to fit in a batch (max size: {maxSizeInBytes} bytes)";

        return remedy.IsEmpty() ? message : $"{message}. {remedy}";
    }

    public Guid EnvelopeId { get; }
    public string? MessageType { get; }
    public long MaxSizeInBytes { get; }

    /// <summary>
    /// GH-4523. The transport-specific guidance folded into <see cref="Exception.Message"/>, or null when the
    /// transport supplied none.
    /// </summary>
    public string? Remedy { get; }
}
