using Wolverine.Runtime.Serialization;

namespace Wolverine.RavenDb.Internals;

public class IncomingMessage
{
    public IncomingMessage()
    {
    }

    public IncomingMessage(Envelope envelope, RavenDbMessageStore store)
    {
        Id = store.IdentityFor(envelope);
        EnvelopeId = envelope.Id;
        Status = envelope.Status;
        OwnerId = envelope.OwnerId;
        ExecutionTime = envelope.ScheduledTime?.ToUniversalTime();
        Attempts = envelope.Attempts;
        // When storing as Handled, don't persist the body - it's just for idempotency checks
        Body = envelope.Status == EnvelopeStatus.Handled ? [] : EnvelopeSerializer.Serialize(envelope);
        MessageType = envelope.MessageType!;
        ReceivedAt = envelope.Destination;

        // GH-5036. A document stored already Handled is kept only for idempotency, and a durability pass
        // deletes it once this has passed
        if (envelope.Status == EnvelopeStatus.Handled)
        {
            KeepUntil = (envelope.KeepUntil ?? DateTimeOffset.UtcNow.Add(store.KeepAfterMessageHandling))
                .ToUniversalTime();
        }
    }

    public Guid EnvelopeId { get; set; }

    public string Id { get; set; } = null!;
    public EnvelopeStatus Status { get; set; } = EnvelopeStatus.Incoming;
    public int OwnerId { get; set; }
    public DateTimeOffset? ExecutionTime { get; set; }
    public int Attempts { get; set; }
    public byte[] Body { get; set; } = [];
    public string MessageType { get; set; } = string.Empty;
    public Uri? ReceivedAt { get; set; }
    public DateTimeOffset? KeepUntil { get; set; }

    public Envelope Read()
    {
        Envelope envelope;
        if (Body == null || Body.Length == 0)
        {
            // For handled envelopes, body is not stored - create a minimal envelope
            envelope = new Envelope
            {
                Id = EnvelopeId,
                MessageType = MessageType,
                Destination = ReceivedAt,
                Data = []
            };
        }
        else
        {
            envelope = EnvelopeSerializer.Deserialize(Body);
        }

        envelope.Id = EnvelopeId;
        envelope.OwnerId = OwnerId;
        envelope.Status = Status;
        envelope.Attempts = Attempts;
        envelope.ScheduledTime = ExecutionTime;
        return envelope;
    }
}