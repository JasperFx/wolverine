using Wolverine.Runtime.Serialization;

namespace Wolverine.EntityFrameworkCore.Internals;

public class OutgoingMessage
{
    public OutgoingMessage()
    {
    }

    public OutgoingMessage(Envelope envelope)
    {
        Id = envelope.Id;
        OwnerId = envelope.OwnerId;
        Attempts = envelope.Attempts;
        Body = EnvelopeSerializer.Serialize(envelope);
        MessageType = envelope.MessageType!;

        Destination = envelope.Destination!.ToString();
        DeliverBy = envelope.DeliverBy?.ToUniversalTime();
    }

    public Guid Id { get; set; }
    public int OwnerId { get; set; }
    public string Destination { get; set; } = "destination";
    public DateTimeOffset? DeliverBy { get; set; }
    public byte[] Body { get; set; } = [];
    public int Attempts { get; set; }
    public string MessageType { get; set; } = string.Empty;
}