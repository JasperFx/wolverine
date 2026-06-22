using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Interop.MassTransit;

/// <summary>
///     Strongly-typed view over a MassTransit "envelope" message. The non-generic base carries the
///     MassTransit transport metadata (ids, addresses, headers); <see cref="MassTransitEnvelope{T}" />
///     adds the deserialized message body. Exposed to user code through hooks such as
///     <see cref="IMassTransitInterop.MapTenantIdFrom{T}" /> so that Wolverine metadata (e.g. the
///     tenant id) can be derived from either the deserialized message body or the surrounding
///     MassTransit transport metadata (headers, addresses, ids).
/// </summary>
public abstract class MassTransitEnvelope
{
    protected MassTransitEnvelope()
    {
    }

    protected MassTransitEnvelope(Envelope envelope)
    {
        MessageId = envelope.Id.ToString();
        CorrelationId = envelope.CorrelationId;
        ConversationId = envelope.ConversationId.ToString();
        SentTime = DateTime.UtcNow;

        var messageType = envelope.Message!.GetType();
        MessageType = [$"urn:message:{messageType.Namespace}:{messageType.NameInCode()}"];

        if (envelope.DeliverBy != null)
        {
            ExpirationTime = envelope.DeliverBy.Value.UtcDateTime;
        }

        foreach (var header in envelope.Headers)
        {
            Headers[header.Key] = header.Value;
        }
    }

    public string? MessageId { get; set; }
    public string? RequestId { get; set; }
    public string? CorrelationId { get; set; }
    public string? ConversationId { get; set; }
    public string? InitiatorId { get; set; }

    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public string? DestinationAddress { get; set; }
    public string? FaultAddress { get; set; }

    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public string[]? MessageType { get; set; }

    public DateTime? ExpirationTime { get; set; }
    public DateTime? SentTime { get; set; }

    public Dictionary<string, object?> Headers { get; set; } = new();

    // Wolverine doesn't care about this on the inbound side, so don't bother deserializing it
    // ReSharper disable once UnusedMember.Global
    public BusHostInfo Host => BusHostInfo.Instance;

    /// <summary>The deserialized message body.</summary>
    public abstract object? Body { get; }

    public string? SourceAddress { get; set; }
    public string? ResponseAddress { get; set; }

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

        foreach (var header in Headers) envelope.Headers[header.Key] = header.Value?.ToString();

        if (ExpirationTime.HasValue)
        {
            envelope.DeliverBy = ExpirationTime.Value.ToUniversalTime();
        }

        if (SentTime.HasValue)
        {
            envelope.SentAt = SentTime.Value.ToUniversalTime();
        }
    }
}

/// <summary>
///     Strongly-typed MassTransit envelope view carrying the deserialized <typeparamref name="T" />
///     message body alongside the MassTransit transport metadata.
/// </summary>
/// <typeparam name="T">The Wolverine message type carried by the MassTransit envelope.</typeparam>
public class MassTransitEnvelope<T> : MassTransitEnvelope where T : class
{
    public MassTransitEnvelope()
    {
    }

    public MassTransitEnvelope(Envelope envelope) : base(envelope)
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
    }

    /// <summary>The deserialized message body.</summary>
    public T? Message { get; set; }

    public override object? Body => Message;
}
