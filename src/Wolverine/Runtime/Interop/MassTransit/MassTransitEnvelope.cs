using System;
using System.Collections.Generic;
using LamarCodeGeneration;
using Microsoft.Extensions.Caching.Memory;

namespace Wolverine.Runtime.Interop.MassTransit;

internal interface IMassTransitEnvelope
{
    object? Body { get; }
    string? ResponseAddress { get; set; }
    string? SourceAddress { get; set; }
    void TransferData(Envelope envelope);
}

internal class MassTransitEnvelope<T> : IMassTransitEnvelope where T : class
{
    public MassTransitEnvelope()
    {
    }

    public MassTransitEnvelope(Envelope envelope)
    {
        if (envelope.Message is T m)
        {
            Message = m;
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(envelope.Message),
                $"Message cannot be cast to {typeof(T).FullNameInCode()}");
        }

        MessageId = envelope.Id.ToString();
        CorrelationId = envelope.CorrelationId;
        ConversationId = envelope.ConversationId.ToString();
        SentTime = DateTime.UtcNow;
        Message = (T)envelope.Message!;

        var messageType = envelope.Message.GetType();
        MessageType = new[] { $"urn:message:{messageType.Namespace}:{messageType.NameInCode()}" };

        if (envelope.DeliverBy != null)
        {
            ExpirationTime = envelope.DeliverBy.Value.UtcDateTime;
        }
    }

    public object? Body => Message;

    public string? MessageId { get; set; }
    public string? RequestId { get; set; }
    public string? CorrelationId { get; set; }
    public string? ConversationId { get; set; }
    public string? InitiatorId { get; set; }
    public string? SourceAddress { get; set; }
    public string? DestinationAddress { get; set; }
    public string? ResponseAddress { get; set; }
    public string? FaultAddress { get; set; }
    public string[]? MessageType { get; set; }

    public T? Message { get; set; }


    public DateTime? ExpirationTime { get; set; }
    public DateTime? SentTime { get; set; }

    public Dictionary<string, object?> Headers { get; set; } = new();

    public void TransferData(Envelope envelope)
    {
        if (MessageId != null && Guid.TryParse(MessageId, out var id))
        {
            envelope.Id = id;
        }

        envelope.CorrelationId = CorrelationId;

        if (ConversationId != null && Guid.TryParse(ConversationId, out var cid))
        {
            envelope.ConversationId = cid;
        }

        foreach (var header in Headers)
        {
            envelope.Headers[header.Key] = header.Value?.ToString();
        }

        if (ExpirationTime.HasValue)
        {
            envelope.DeliverBy = ExpirationTime.Value.ToUniversalTime();
        }

        if (SentTime.HasValue)
        {
            envelope.SentAt = SentTime.Value.ToUniversalTime();
        }
    }

    // Wolverine doesn't care about this, so don't bother deserializing it
    public BusHostInfo? Host => BusHostInfo.Instance;
}

[Serializable]
internal class MassTransitEnvelope : MassTransitEnvelope<object>
{
    public MassTransitEnvelope()
    {
    }

    public MassTransitEnvelope(Envelope envelope) : base(envelope)
    {
    }
}
