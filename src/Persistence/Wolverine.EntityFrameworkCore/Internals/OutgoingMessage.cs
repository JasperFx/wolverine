using System;

namespace Wolverine.EntityFrameworkCore.Internals;

public class OutgoingMessage
{
    public Guid Id { get; set; }
    public int OwnerId { get; set; }
    public string Destination { get; set; }
    public DateTimeOffset? DeliverBy { get; set; }
    public byte[] Body { get; set; } = Array.Empty<byte>();
    public int Attempts { get; set; }
    public Guid? ConversationId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string? ParentId { get; set; }
    public string? SagaId { get; set; }
    public string MessageType { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string? ReplyRequested { get; set; }
    public bool AckRequested { get; set; }
    public string? ReplyUri { get; set; }
    public DateTimeOffset SentAt { get; set; }

    public OutgoingMessage()
    {
    }

    public OutgoingMessage(Envelope envelope)
    {
        Id = envelope.Id;
        OwnerId = envelope.OwnerId;
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

        Destination = envelope.Destination!.ToString();
        DeliverBy = envelope.DeliverBy;
        SentAt = envelope.SentAt;
    }

    
}