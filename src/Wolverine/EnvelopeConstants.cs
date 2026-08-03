namespace Wolverine;

public static class EnvelopeConstants
{
    public const string JsonContentType = "application/json";
    public const string CorrelationIdKey = "correlation-id";
    public const string SagaIdKey = "saga-id";
    public const string IdKey = "id";
    public const string ConversationIdKey = "conversation-id";
    public const string ContentTypeKey = "content-type";
    public const string SourceKey = "source";
    public const string ReplyRequestedKey = "reply-requested";
    public const string DestinationKey = "destination";
    public const string ReplyUriKey = "reply-uri";
    public const string ExecutionTimeKey = "time-to-send";
    public const string SentAtKey = "sent-at";
    public const string AttemptsKey = "attempts";
    public const string AckRequestedKey = "ack-requested";
    public const string MessageTypeKey = "message-type";
    public const string AcceptedContentTypesKey = "accepted-content-types";
    public const string DeliverByKey = "deliver-by";
    public const string ParentIdKey = "parent-id";
    public const string IsResponseKey = "is-response";
    public const string TenantIdKey = "tenant-id";
    public const string GroupIdKey = "group-id";
    public const string DeduplicationIdKey = "deduplication-id";
    public const string PartitionKey = "partition-key";
    public const string TopicNameKey = "topic-name";
    public const string KeepUntilKey = "keep-until";
    public const string UserNameKey = "user-name";
    public const string CausationIdKey = "causation-id";

    /// <summary>
    ///     The format <see cref="Wolverine.Transports.EnvelopeMapper{TIncoming,TOutgoing}" /> uses when it writes
    ///     <see cref="DateTimeOffset" /> envelope properties (<c>time-to-send</c>, <c>deliver-by</c>) into
    ///     transport headers. It is deliberately NOT round-trippable by <c>DateTime.Parse</c> or
    ///     <c>DateTimeOffset.TryParse</c> — the ':' before the fractional seconds and the bare trailing 'Z'
    ///     are not recognized by either. Wolverine's own binary envelope format
    ///     (<see cref="Wolverine.Runtime.Serialization.EnvelopeSerializer" />) writes <c>"o"</c> instead, so
    ///     any reader that can see values from both sources has to try this format explicitly. See GH-3613.
    /// </summary>
    internal const string TransportHeaderDateTimeFormat = "yyyy-MM-dd HH:mm:ss:ffffff Z";
}