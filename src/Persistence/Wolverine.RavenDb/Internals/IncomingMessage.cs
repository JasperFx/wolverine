using Wolverine.Runtime.Serialization;

namespace Wolverine.RavenDb.Internals;

public class IncomingMessage
{
    public IncomingMessage()
    {
    }

    public IncomingMessage(Envelope envelope)
    {
        Id = envelope.Id.ToString();
        Status = envelope.Status.ToString();
        OwnerId = envelope.OwnerId;
        ExecutionTime = envelope.ScheduledTime?.ToUniversalTime();
        Attempts = envelope.Attempts;
        Body = EnvelopeSerializer.Serialize(envelope);
        MessageType = envelope.MessageType!;
        ReceivedAt = envelope.Destination?.ToString();
    }

    public string Id { get; set; }
    public string Status { get; set; } = EnvelopeStatus.Incoming.ToString();
    public int OwnerId { get; set; }
    public DateTimeOffset? ExecutionTime { get; set; }
    public int Attempts { get; set; }
    public byte[] Body { get; set; } = [];
    public string MessageType { get; set; } = string.Empty;
    public string? ReceivedAt { get; set; }

    internal Envelope Read() => EnvelopeSerializer.Deserialize(Body);
}