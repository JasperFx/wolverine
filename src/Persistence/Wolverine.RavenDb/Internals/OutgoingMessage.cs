using Wolverine.Runtime.Serialization;

namespace Wolverine.RavenDb.Internals;

public class OutgoingMessage
{
    public OutgoingMessage()
    {
    }

    public OutgoingMessage(Envelope envelope)
    {
        Id = envelope.Id.ToString();
        OwnerId = envelope.OwnerId;
        Attempts = envelope.Attempts;
        Body = EnvelopeSerializer.Serialize(envelope);
        MessageType = envelope.MessageType!;

        Destination = envelope.Destination;
        DeliverBy = envelope.DeliverBy?.ToUniversalTime();
    }

    public string Id { get; set; }
    public int OwnerId { get; set; }
    public Uri Destination { get; set; }
    public DateTimeOffset? DeliverBy { get; set; }
    public byte[] Body { get; set; } = [];
    public int Attempts { get; set; }
    public string MessageType { get; set; } = string.Empty;

    public Envelope Read()
    {
        var envelope = EnvelopeSerializer.Deserialize(Body);
        envelope.OwnerId = OwnerId;
        return envelope;
    }
}