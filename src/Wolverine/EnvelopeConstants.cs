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
    ///     The W3C id of the failed processing attempt that an error handling continuation retried, requeued or
    ///     rescheduled. The next attempt's span carries it as an <see cref="System.Diagnostics.ActivityLink" />.
    ///     Deliberately a loose <see cref="Envelope.Headers" /> entry rather than a mapped envelope property, so an
    ///     envelope that is never retried pays nothing for it in any transport mapper or in the envelope
    ///     serializer. See GH-4398.
    /// </summary>
    public const string PreviousAttemptActivityIdKey = "previous-attempt-activity-id";

    /// <summary>
    ///     Legacy timestamp format retained for reading older transport headers and persisted envelopes.
    ///     New transport headers use the invariant round-trip format ("o").
    /// </summary>
    internal const string TransportHeaderDateTimeFormat = "yyyy-MM-dd HH:mm:ss:ffffff Z";
}