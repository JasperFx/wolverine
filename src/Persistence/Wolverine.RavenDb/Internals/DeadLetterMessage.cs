using JasperFx.Core.Reflection;
using Wolverine.Persistence.Durability;
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
        MessageType = envelope.MessageType;
        ReceivedAt = envelope.Destination;
        SentAt = envelope.SentAt;
        ScheduledTime = envelope.ScheduledTime;
        Source = envelope.Source;
        ExceptionType = exception?.GetType().FullNameInCode();
        ExceptionMessage = exception?.Message;
        
        // TODO -- need to harden this one
        Body = EnvelopeSerializer.Serialize(envelope);
    }

    public DateTimeOffset? ScheduledTime { get; set; }

    // Maps to the Envelope.Id
    public string Id { get; set; }
    public Guid EnvelopeId { get; set; }
    public string MessageType { get; set; }
    public Uri ReceivedAt { get; set; }
    public string Source { get; set; }
    public string ExceptionType { get; set; }
    public string ExceptionMessage { get; set; }
    public DateTimeOffset? SentAt { get; set; } 
    public bool Replayable { get; set; }
    public byte[] Body { get; set; }
    public DateTimeOffset ExpirationTime { get; set; }

    public DeadLetterEnvelope ToEnvelope()
    {
        var envelope = EnvelopeSerializer.Deserialize(Body);
        return new DeadLetterEnvelope(EnvelopeId, ScheduledTime, envelope, MessageType, ReceivedAt.ToString(), Source, ExceptionType, ExceptionMessage, SentAt.Value, Replayable);
    }
}