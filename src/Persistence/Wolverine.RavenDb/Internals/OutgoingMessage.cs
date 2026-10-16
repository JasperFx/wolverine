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
        Timestamp = DateTimeOffset.UtcNow;
    }

    public string Id { get; set; } = null!;
    public int OwnerId { get; set; }
    public Uri? Destination { get; set; }
    public DateTimeOffset? DeliverBy { get; set; }
    public byte[] Body { get; set; } = [];
    public int Attempts { get; set; }
    public string MessageType { get; set; } = string.Empty;

    /// <summary>
    /// GH-5016. When this outbox document was written, for the stale owner-0 query of a
    /// <see cref="DurabilityMode.ServerlessWithOutbox"/> durability pass. Documents written before this
    /// property existed do not have the field at all, and the stale query takes those as always stale.
    /// See GH-5036
    /// </summary>
    public DateTimeOffset? Timestamp { get; set; }

    public Envelope Read()
    {
        var envelope = EnvelopeSerializer.Deserialize(Body);
        envelope.OwnerId = OwnerId;
        envelope.Attempts = Attempts;
        return envelope;
    }
}