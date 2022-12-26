using Wolverine.Runtime.Serialization;

namespace Wolverine.Transports;

public class OutgoingMessageBatch
{
    public OutgoingMessageBatch(Uri destination, IReadOnlyList<Envelope> messages)
    {
        Destination = destination;
        var messagesList = new List<Envelope>();
        messagesList.AddRange(messages);
        Messages = messagesList;

        foreach (var message in messages) message.Destination = destination;

        Data = EnvelopeSerializer.Serialize(Messages);
    }

    public byte[] Data { get; set; }

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