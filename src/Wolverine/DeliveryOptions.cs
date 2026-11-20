using JasperFx.Core;
using Wolverine.Util;

namespace Wolverine;

/// <summary>
///     Optional customizations and metadata for how a message should be delivered
/// </summary>
public class DeliveryOptions
{
    private Dictionary<string, string?>? _headers;

    /// <summary>
    ///     Optional metadata about this message
    /// </summary>
    public Dictionary<string, string?> Headers
    {
        get => _headers ??= new();
        internal set => _headers = value;
    }

    /// <summary>
    ///     Instruct Wolverine to throw away this message if it is not successfully sent and processed
    ///     by the time specified
    /// </summary>
    public DateTimeOffset? DeliverBy { get; set; }

    /// <summary>
    ///     Is an acknowledgement requested
    /// </summary>
    public bool? AckRequested { get; set; }

    /// <summary>
    ///     Used by scheduled jobs or transports with a native scheduled send functionality to have this message processed by
    ///     the receiving application at or after the designated time
    /// </summary>
    public DateTimeOffset? ScheduledTime { get; set; }

    /// <summary>
    ///     Set the DeliverBy property to have this message thrown away
    ///     if it cannot be sent before the allotted time
    /// </summary>
    /// <value></value>
    public TimeSpan DeliverWithin
    {
        set => DeliverBy = DateTimeOffset.UtcNow.Add(value);
    }

    /// <summary>
    ///     Set the ScheduleTime to now plus the value of the supplied TimeSpan
    /// </summary>
    public TimeSpan? ScheduleDelay { get; set; }

    /// <summary>
    ///     Declare that this application is interested in receiving
    ///     a response of this message type upon message receipt
    /// </summary>
    public Type? ResponseType { get; set; }

    /// <summary>
    ///     If this message is part of a stateful saga, this property identifies
    ///     the underlying saga state object
    /// </summary>
    public string? SagaId { get; set; }

    /// <summary>
    ///     Override the tenant id metadata for a single envelope
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    ///     Override the correlation id stamped on the outgoing envelope. Defaults to
    ///     the sending <see cref="IMessageContext"/>'s correlation id when not set.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    ///     Override the causation id stamped on the outgoing envelope. Defaults to
    ///     the sending <see cref="IMessageContext"/>'s conversation id when not set.
    /// </summary>
    public string? CausationId { get; set; }

    /// <summary>
    ///     Mimetype of the serialized data
    /// </summary>
    public string? ContentType { get; set; }

    internal bool IsResponse { get; set; }
    
    /// <summary>
    /// Extra routing information to send to the eventual
    /// Wolverine transport. Mostly built for WebSockets
    /// </summary>
    public object? RoutingInformation { get; set; }

    internal void Override(Envelope envelope)
    {
        if (_headers is { Count: > 0 })
        {
            foreach (var header in _headers) envelope.Headers[header.Key] = header.Value;

            // GH-5553: ReturnFaultToSender() names the reply address once it is known. The route has set it by now;
            // when it has not, the receiver reads the envelope's reply address instead
            if (envelope.ReplyUri != null && _headers.TryGetValue(FaultHeaders.ReplyTo, out var replyTo) &&
                replyTo == FaultHeaders.ReplyToSender)
            {
                envelope.Headers[FaultHeaders.ReplyTo] = envelope.ReplyUri.ToString();
            }
        }

        // The status should be a state machine set by the deliver by or scheduled time setters
        if (DeliverBy.HasValue)
        {
            envelope.DeliverBy = DeliverBy;
        }

        // GH-5676: a time already in the past (DateTimeOffset.MinValue is a common "right away") or a delay of zero is
        // sent right away, so the envelope is not marked Scheduled. Marked anyway, it was sent right away and still
        // recorded as scheduled, and a tracked session's playback could run it a second time
        if (ScheduledTime.HasValue)
        {
            envelope.ScheduledTime = ScheduledTime;
            if (ScheduledTime.Value > DateTimeOffset.UtcNow) envelope.Status = EnvelopeStatus.Scheduled;
        }

        if (ScheduleDelay.HasValue)
        {
            envelope.ScheduleDelay = ScheduleDelay;
            if (ScheduleDelay.Value > TimeSpan.Zero) envelope.Status = EnvelopeStatus.Scheduled;
        }

        if (AckRequested.HasValue)
        {
            envelope.AckRequested = AckRequested.Value;
        }

        if (ResponseType != null)
        {
            envelope.ReplyRequested = ResponseType.ToMessageTypeName();
        }

        if (SagaId != null)
        {
            envelope.SagaId = SagaId;
        }

        if (TenantId.IsNotEmpty())
        {
            envelope.TenantId = TenantId;
        }

        if (CorrelationId.IsNotEmpty())
        {
            envelope.CorrelationId = CorrelationId;
        }

        if (CausationId.IsNotEmpty())
        {
            // Wolverine's native causation chain is Envelope.ConversationId (Guid), but
            // application-meaningful CausationId values are arbitrary strings (matching
            // JasperFx.IMetadataContext). Stash it as a well-known header so downstream
            // consumers — most notably Wolverine.Marten's OutboxedSessionFactory when it
            // configures session.CausationId — can surface it without collapsing the
            // Guid-typed ConversationId in the process.
            envelope.Headers[EnvelopeConstants.CausationIdKey] = CausationId;
        }

        if (ContentType != null)
        {
            envelope.ContentType = ContentType;
        }

        if (IsResponse)
        {
            envelope.IsResponse = true;
        }

        if (GroupId.IsNotEmpty())
        {
            envelope.GroupId = GroupId;
        }

        if (DeduplicationId.IsNotEmpty())
        {
            envelope.DeduplicationId = DeduplicationId;
        }

        if (PartitionKey.IsNotEmpty())
        {
            envelope.PartitionKey = PartitionKey;
        }

        if (RoutingInformation != null)
        {
            envelope.RoutingInformation = RoutingInformation;
        }

        // GH-5537: null leaves whatever a [StartsNewLinkedTrace] / opts.Tracing rule decided; an
        // explicit true or false wins over the rule
        if (StartsNewLinkedTrace.HasValue)
        {
            envelope.StartsNewLinkedTrace = StartsNewLinkedTrace.Value;
        }
    }

    /// <summary>
    /// Application defined message group identifier. Part of AMQP 1.0 spec as the "group-id" property. Session identifier
    /// for Azure Service Bus. MessageGroupId for Amazon SQS FIFO Queue
    /// </summary>
    public string? GroupId { get; set; }

    /// <summary>
    /// GH-4180. The application defined *logical* identity of this message, enforced by
    /// <c>[Deduplicated]</c> handlers and endpoints. Also used as the native MessageDeduplicationId
    /// for Amazon SQS/SNS FIFO and GCP Pub/Sub. Setting it here always wins over any rule that
    /// derives an id from the message itself. See <see cref="Envelope.DeduplicationId"/>.
    /// </summary>
    public string? DeduplicationId { get; set; }
    
    /// <summary>
    /// Partition Key when sending via Kafka
    /// </summary>
    public string? PartitionKey { get; set; }

    /// <summary>
    /// Start a new trace on the receiving side, linked back to the sending activity. Null (the default)
    /// leaves the decision to <c>[StartsNewLinkedTrace]</c>, <see cref="IStartsNewLinkedTrace" /> or
    /// <c>opts.Tracing.StartNewLinkedTraceFor()</c>; true or false wins over those rules.
    /// </summary>
    public bool? StartsNewLinkedTrace { get; set; }

    /// <summary>
    ///     Add a header key/value pair to the outgoing message
    /// </summary>
    /// <param name="key"></param>
    /// <param name="headerValue"></param>
    /// <returns></returns>
    public DeliveryOptions WithHeader(string key, string headerValue)
    {
        Headers[key] = headerValue;
        return this;
    }

    /// <summary>
    ///     GH-5553. If this message fails for good on the receiving side (its retries used up and it is
    ///     dead-lettered), send its <see cref="Fault{T}" /> to <paramref name="destination" />, even when the
    ///     receiver has not turned on <c>PublishFaultEvents()</c>. With fault events on, the fault also goes
    ///     wherever the receiver routes it, but only once to any one address
    /// </summary>
    /// <remarks>
    ///     Sets the <see cref="FaultHeaders.ReplyTo" /> header. A receiver on a Wolverine version that predates it
    ///     ignores the header and routes the fault as it always has, which with fault events off means not at all
    /// </remarks>
    public DeliveryOptions FaultTo(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Headers[FaultHeaders.ReplyTo] = destination.ToString();
        return this;
    }

    /// <summary>
    ///     GH-5553. If this message fails for good on the receiving side (its retries used up and it is
    ///     dead-lettered), send its <see cref="Fault{T}" /> back to the sender's reply address
    ///     (<see cref="Envelope.ReplyUri" />), even when the receiver has not turned on
    ///     <c>PublishFaultEvents()</c>. The sender needs a listener the receiver can reach, and a handler for the
    ///     <see cref="Fault{T}" />
    /// </summary>
    /// <remarks>
    ///     Sets the <see cref="FaultHeaders.ReplyTo" /> header to <see cref="FaultHeaders.ReplyToSender" />. A
    ///     receiver on a Wolverine version that predates it ignores the header
    /// </remarks>
    public DeliveryOptions ReturnFaultToSender()
    {
        Headers[FaultHeaders.ReplyTo] = FaultHeaders.ReplyToSender;
        return this;
    }

    /// <summary>
    ///     Shortcut to build DeliveryOptions requiring the specified response type
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static DeliveryOptions RequireResponse<T>()
    {
        return new DeliveryOptions
        {
            ResponseType = typeof(T)
        };
    }
}