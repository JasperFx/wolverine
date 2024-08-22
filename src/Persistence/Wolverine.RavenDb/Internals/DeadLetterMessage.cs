using JasperFx.Core.Reflection;
using Wolverine.Runtime.Serialization;

namespace Wolverine.RavenDb.Internals;

public class DeadLetterMessage
{
    public DeadLetterMessage()
    {
    }

    public DeadLetterMessage(Envelope envelope, Exception? exception)
    {
        Id = envelope.Id.ToString();
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
    public string MessageType { get; set; }
    public Uri ReceivedAt { get; set; }
    public string Source { get; set; }
    public string ExceptionType { get; set; }
    public string ExceptionMessage { get; set; }
    public DateTimeOffset? SentAt { get; set; } 
    public bool Replayable { get; set; }
    public byte[] Body { get; set; }
}