using Wolverine.Runtime.Serialization;

namespace Wolverine.Transports;

public class OutgoingMessageBatch
{
    public OutgoingMessageBatch(Uri destination, IReadOnlyList<Envelope> messages)
    {
        Destination = destination;
        Messages = new List<Envelope>(messages);

        foreach (var message in messages) message.Destination = destination;

    }

    private byte[]? _data;

    /// <summary>
    ///     The whole batch serialized as one contiguous buffer. Materialized on first read rather
    ///     than in the constructor: only the TCP protocol consumes it, while every other transport
    ///     reads <see cref="Envelope.Data" /> per message and chunks to its own limits.
    ///     Building it eagerly meant every SQS, Rabbit or Service Bus batch paid for a buffer it
    ///     never read -- and at MessageBatchSize 100 with large payloads that single allocation is
    ///     enough to take the process down with an OutOfMemoryException from
    ///     <c>MemoryStream.set_Capacity</c>, long before the heap itself is anywhere near full.
    /// </summary>
    public byte[] Data
    {
        get => _data ??= EnvelopeSerializer.Serialize(Messages);
        set => _data = value;
    }

    public Uri Destination { get; }

    public IList<Envelope> Messages { get; }

    public override string ToString()
    {
        return $"Outgoing batch to {Destination} with {Messages.Count} messages";
    }

    public static OutgoingMessageBatch ForPing(Uri destination)
    {
        var envelope = Envelope.ForPing(destination);

        return new OutgoingMessageBatch(destination, new[] { envelope });
    }
}