using JasperFx.Core.Reflection;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;

namespace Wolverine.RavenDb.Internals;

public class DeadLetterMessage
{
    public DeadLetterMessage()
    {
    }

    public DeadLetterMessage(Envelope envelope, Exception? exception)
    {
        Id = "dlq/" + envelope.Id.ToString();
        EnvelopeId = envelope.Id;
        MessageType = envelope.MessageType!;
        ReceivedAt = envelope.Destination!;
        SentAt = envelope.SentAt;
        ScheduledTime = envelope.ScheduledTime;
        Source = envelope.Source!;
        ExceptionType = exception.DeadLetterExceptionType()!;
        ExceptionMessage = exception.DeadLetterExceptionMessage()!;

        // TODO -- need to harden this one
        Body = EnvelopeSerializer.Serialize(envelope);
    }

    public DateTimeOffset? ScheduledTime { get; set; }

    // Maps to the Envelope.Id
    public string Id { get; set; } = null!;
    public Guid EnvelopeId { get; set; }
    public string MessageType { get; set; } = null!;
    public Uri ReceivedAt { get; set; } = null!;
    public string Source { get; set; } = null!;
    public string ExceptionType { get; set; } = null!;
    public string ExceptionMessage { get; set; } = null!;
    public DateTimeOffset? SentAt { get; set; }
    public bool Replayable { get; set; }
    public byte[] Body { get; set; } = null!;
    public DateTimeOffset ExpirationTime { get; set; }

    public DeadLetterEnvelope ToEnvelope()
    {
        return new DeadLetterEnvelope(EnvelopeId, ScheduledTime, readBody(), MessageType,
            ReceivedAt?.ToString() ?? string.Empty, Source, ExceptionType, ExceptionMessage,
            SentAt ?? default, Replayable);
    }

    // Same placeholder contract as DatabasePersistence.ReadDeadLetterBodyAsync (CritterWatch#902,
    // GH-3773): a document whose stored body was never written by EnvelopeSerializer — hand-seeded
    // raw JSON, or an incompatible serialization version — must cost the operator that row, not the
    // whole queue read. The scalar fields on this document still describe the poison row.
    private Envelope readBody()
    {
        if (Body is { Length: > 0 })
        {
            try
            {
                return EnvelopeSerializer.Deserialize(Body);
            }
            catch (Exception)
            {
                // fall through to the placeholder
            }
        }

        return new Envelope
        {
            Id = EnvelopeId,
            MessageType = MessageType,
            Message = new PlaceHolder(),
            Destination = ReceivedAt
        };
    }
}