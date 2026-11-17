using Wolverine.RDBMS;
using Wolverine.Runtime.Serialization;

namespace Wolverine.EntityFrameworkCore.Internals;

public class IncomingMessage
{
    public IncomingMessage()
    {
    }

    public IncomingMessage(Envelope envelope)
    {
        Id = envelope.Id;
        Status = envelope.Status.ToString();
        OwnerId = envelope.OwnerId;
        ExecutionTime = envelope.ScheduledTime?.ToUniversalTime();
        Attempts = envelope.Attempts;
        Body = envelope.Status == EnvelopeStatus.Handled ? [] : EnvelopeSerializer.Serialize(envelope);
        MessageType = envelope.MessageType!;
        ReceivedAt = envelope.Destination?.ToString();
        KeepUntil = envelope.KeepUntil;
        GroupId = DatabaseConstants.StorableGroupId(envelope);
    }

    public DateTimeOffset? KeepUntil { get; set; }

    public Guid Id { get; set; }
    public string Status { get; set; } = EnvelopeStatus.Incoming.ToString();
    public int OwnerId { get; set; }
    public DateTimeOffset? ExecutionTime { get; set; }
    public int Attempts { get; set; }
    public byte[] Body { get; set; } = [];
    public string MessageType { get; set; } = string.Empty;
    public string? ReceivedAt { get; set; }

    /// <summary>
    /// GH-5167. The envelope's group id, for scheduled message management to query by
    /// </summary>
    public string? GroupId { get; set; }
}