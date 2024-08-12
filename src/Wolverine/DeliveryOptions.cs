using JasperFx.Core;
using Wolverine.Util;

namespace Wolverine;

/// <summary>
///     Optional customizations and metadata for how a message should be delivered
/// </summary>
public class DeliveryOptions
{
    /// <summary>
    ///     Optional metadata about this message
    /// </summary>
    public Dictionary<string, string?> Headers { get; internal set; } = new();

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
    public string? SagaId { get; internal set; }

    /// <summary>
    ///     Override the tenant id metadata for a single envelope
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    ///     Mimetype of the serialized data
    /// </summary>
    public string? ContentType { get; set; }

    internal bool IsResponse { get; set; }

    internal void Override(Envelope envelope)
    {
        foreach (var header in Headers) envelope.Headers[header.Key] = header.Value;

        // The status should be a state machine set by the deliver by or scheduled time setters
        if (DeliverBy.HasValue)
        {
            envelope.DeliverBy = DeliverBy;
        }

        if (ScheduledTime.HasValue)
        {
            envelope.ScheduledTime = ScheduledTime;
            envelope.Status = EnvelopeStatus.Scheduled;
        }

        if (ScheduleDelay.HasValue)
        {
            envelope.ScheduleDelay = ScheduleDelay;
            envelope.Status = EnvelopeStatus.Scheduled;
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

        if (Key.IsNotEmpty())
        {
            envelope.Key = Key;
        }
    }

    /// <summary>
    /// Application defined message group identifier. Part of AMQP 1.0 spec as the "group-id" property. Session identifier
    /// for Azure Service Bus. MessageGroupId for Amazon SQS FIFO Queue
    /// </summary>
    public string? GroupId { get; set; }

    /// <summary>
    /// MessageDeduplicationId for Amazon SQS FIFO Queue
    /// </summary>
    public string? DeduplicationId { get; set; }
    /// <summary>
    /// Message key for kafka
    /// </summary>
    public string? Key { get; set; }

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