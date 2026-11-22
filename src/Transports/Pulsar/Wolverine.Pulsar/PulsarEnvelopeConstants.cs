namespace Wolverine.Pulsar;

public static class PulsarEnvelopeConstants
{
    public const string ReconsumeTimes = "RECONSUMETIMES";
    public const string DelayTimeMetadataKey = "DELAY_TIME";
    public const string RealTopicMetadataKey = "REAL_TOPIC";
    public const string TopicMetadataKey = "TOPIC_NAME";
    public const string MessageIdMetadataKey = "MESSAGE_ID";
    public const string RealSubscriptionMetadataKey = "REAL_SUBSCRIPTION";
    public const string OriginMessageIdMetadataKey = "ORIGIN_MESSAGE_ID";
    public const string Exception = "EXCEPTION";

    /// <summary>
    ///     GH-5813. The topic a message was first consumed from by a listener over several topics (<c>Topics()</c>
    ///     or <c>TopicsPattern()</c>), carried through the retry-letter topic so a requeue of the retried message
    ///     goes back to that topic rather than the listener's primary topic.
    /// </summary>
    internal const string SourceTopicMetadataKey = "WOLVERINE_SOURCE_TOPIC";
}