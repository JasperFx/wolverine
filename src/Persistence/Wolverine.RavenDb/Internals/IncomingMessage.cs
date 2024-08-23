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
        EnvelopeId = envelope.Id;
        Status = envelope.Status;
        OwnerId = envelope.OwnerId;
        ExecutionTime = envelope.ScheduledTime?.ToUniversalTime();
        Attempts = envelope.Attempts;
        Body = EnvelopeSerializer.Serialize(envelope);
        MessageType = envelope.MessageType!;
        ReceivedAt = envelope.Destination;
    }

    public Guid EnvelopeId { get; set; }

    public string Id { get; set; }
    public EnvelopeStatus Status { get; set; } = EnvelopeStatus.Incoming;
    public int OwnerId { get; set; }
    public DateTimeOffset? ExecutionTime { get; set; }
    public int Attempts { get; set; }
    public byte[] Body { get; set; } = [];
    public string MessageType { get; set; } = string.Empty;
    public Uri? ReceivedAt { get; set; }

    public Envelope Read()
    {
        var envelope = EnvelopeSerializer.Deserialize(Body);
        envelope.Id = EnvelopeId;
        envelope.OwnerId = OwnerId;
        envelope.Status = Status;
        envelope.Attempts = Attempts;
        envelope.ScheduledTime = ExecutionTime;
        return envelope;
        

    }
}