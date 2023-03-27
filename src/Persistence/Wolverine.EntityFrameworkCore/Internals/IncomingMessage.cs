using Wolverine.Runtime.Serialization;

namespace Wolverine.EntityFrameworkCore.Internals;

public class IncomingMessage
{
    public IncomingMessage()
    {
    }

    public IncomingMessage(Envelope envelope)
    {
        // TODO -- thin this down!
        Id = envelope.Id;
        Status = envelope.Status.ToString();
        OwnerId = envelope.OwnerId;
        ExecutionTime = envelope.ScheduledTime?.ToUniversalTime();
        Attempts = envelope.Attempts;
        Body = EnvelopeSerializer.Serialize(envelope);
        MessageType = envelope.MessageType;
        ReceivedAt = envelope.Destination?.ToString();
    }

    public Guid Id { get; set; }
    public string Status { get; set; } = EnvelopeStatus.Incoming.ToString();
    public int OwnerId { get; set; }
    public DateTimeOffset? ExecutionTime { get; set; }
    public int Attempts { get; set; }
    public byte[] Body { get; set; } = Array.Empty<byte>();
    public string MessageType { get; set; } = string.Empty;
    public string? ReceivedAt { get; set; }
}