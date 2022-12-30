using System;

namespace Wolverine.EntityFrameworkCore.Internals;

public class IncomingMessage
{
    public Guid Id { get; set; }
    public string Status { get; set; } = EnvelopeStatus.Incoming.ToString();
    public int OwnerId { get; set; }
    public DateTimeOffset? ExecutionTime { get; set; }
    public int Attempts { get; set; }
    public byte[] Body { get; set; } = Array.Empty<byte>();
    public Guid? ConversationId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string? ParentId { get; set; }
    public string? SagaId { get; set; }
    public string MessageType { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string? ReplyRequested { get; set; }
    public bool AckRequested { get; set; }
    public string? ReplyUri { get; set; } 
    public string? ReceivedAt { get; set; } 
    public DateTimeOffset? SentAt { get; set; }

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
        Body = envelope.Data;
        ConversationId = envelope.ConversationId;
        CorrelationId = envelope.CorrelationId;
        ParentId = envelope.ParentId;
        SagaId = envelope.SagaId;
        MessageType = envelope.MessageType;
        ContentType = envelope.ContentType;
        ReplyRequested = envelope.ReplyRequested;
        AckRequested = envelope.AckRequested;
        ReplyUri = envelope.ReplyUri?.ToString();
        ReceivedAt = envelope.Destination?.ToString();
        SentAt = envelope.SentAt.ToUniversalTime();
    }
}