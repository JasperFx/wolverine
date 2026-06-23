using System.Buffers;
using System.Text.RegularExpressions;
using DotPulsar;
using DotPulsar.Abstractions;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Pulsar;

public class PulsarEndpoint : Endpoint<IPulsarEnvelopeMapper, PulsarEnvelopeMapper>
{
    public const string Persistent = "persistent";
    public const string NonPersistent = "non-persistent";
    public const string DefaultNamespace = "tenant";
    public const string Public = "public";
    private readonly PulsarTransport _parent;

    public PulsarEndpoint(Uri uri, PulsarTransport parent) : base(uri, EndpointRole.Application)
    {
        _parent = parent;
        Parse(uri);
        BrokerRole = "topic";
    }

    protected override PulsarEnvelopeMapper buildMapper(IWolverineRuntime runtime)
    {
        return new PulsarEnvelopeMapper(this, runtime);
    }

    public string Persistence { get; private set; } = Persistent;
    public string Tenant { get; private set; } = Public;
    public string Namespace { get; private set; } = DefaultNamespace;
    public string? TopicName { get; private set; }
    public string SubscriptionName { get; internal set; } = "Wolverine";
    public SubscriptionType SubscriptionType { get; internal set; } = SubscriptionType.Exclusive;

    /// <summary>
    ///     Where a brand-new subscription starts consuming: <see cref="SubscriptionInitialPosition.Latest"/>
    ///     (default — only messages published after the subscription is created) or
    ///     <see cref="SubscriptionInitialPosition.Earliest"/> (replay from the start of the topic's
    ///     retained backlog). Only applies on the first read of a not-yet-existing subscription.
    /// </summary>
    public SubscriptionInitialPosition SubscriptionInitialPosition { get; internal set; } =
        SubscriptionInitialPosition.Latest;

    /// <summary>
    ///     Additional native Pulsar topic paths (e.g. <c>persistent://public/default/other</c>) that a
    ///     single listener consumes alongside its primary topic. Pulsar supports one consumer over many
    ///     topics; analogue of Kafka topic groups. Empty by default (single-topic listener).
    /// </summary>
    internal List<string> AdditionalTopics { get; } = new();

    /// <summary>
    ///     When set, the listener subscribes to every topic matching this regex pattern instead of an
    ///     explicit topic (or topic list). Pulsar pattern subscription.
    /// </summary>
    internal Regex? TopicsPattern { get; set; }

    /// <summary>
    ///     Which topics a <see cref="TopicsPattern"/> subscription matches (persistent, non-persistent,
    ///     or all). Defaults to persistent-only.
    /// </summary>
    internal RegexSubscriptionMode RegexSubscriptionMode { get; set; } = RegexSubscriptionMode.Persistent;

    /// <summary>
    ///     The full set of native topic paths this listener subscribes to explicitly: the primary topic
    ///     plus any <see cref="AdditionalTopics"/>. Not used when <see cref="TopicsPattern"/> is set.
    /// </summary>
    internal IReadOnlyList<string> AllTopicPaths() => [PulsarTopic(), .. AdditionalTopics];

    public bool EnableRequeue { get; internal set; } = true;
    public bool UnsubscribeOnClose { get; internal set; } = true;

    /// <summary>
    ///     When true, this listener consumes via an ephemeral, non-durable Pulsar <c>Reader</c> starting
    ///     at the tail (<see cref="DotPulsar.MessageId.Latest"/>) instead of a durable subscription, so
    ///     every node receives all messages published after it joins and never replays history (GH-3184).
    ///     Set via <c>TailFromLatest()</c>.
    /// </summary>
    internal bool IsHotTail { get; set; }

    /// <summary>
    ///     When true, a requeue/defer of a single message uses Pulsar's native per-message
    ///     redelivery (<c>RedeliverUnacknowledgedMessages([messageId])</c>) — the message is left
    ///     unacknowledged and Pulsar redelivers that one message, preserving its redelivery count —
    ///     instead of the default behavior of acknowledging and re-publishing a fresh copy to the
    ///     source topic. Delayed/backoff redelivery is handled by the retry-letter topics (#3182).
    /// </summary>
    public bool UseNativeRedelivery { get; internal set; }

    /// <summary>
    ///     How this listener acknowledges completed messages: individually (default), cumulatively, or
    ///     batched. See <see cref="PulsarAckStrategy"/>.
    /// </summary>
    public PulsarAckStrategy AckStrategy { get; internal set; } = PulsarAckStrategy.Individual;

    /// <summary>
    ///     For <see cref="PulsarAckStrategy.Batched"/>: flush the pending acknowledgments once this many
    ///     messages have completed. Default 100.
    /// </summary>
    public int AckBatchSize { get; internal set; } = 100;

    /// <summary>
    ///     For <see cref="PulsarAckStrategy.Batched"/>: also flush pending acknowledgments at least this
    ///     often, even if the batch size has not been reached. Default 1 second; set to
    ///     <see cref="TimeSpan.Zero"/> to flush only by count.
    /// </summary>
    public TimeSpan AckBatchInterval { get; internal set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    ///     Optional hook to customize the DotPulsar consumer for this listener (consumer name,
    ///     receive-queue size, priority level, properties, etc.) immediately before it is created.
    /// </summary>
    internal Action<IConsumerBuilder<ReadOnlySequence<byte>>>? ConfigureConsumer { get; set; }

    /// <summary>
    ///     Optional hook to customize the DotPulsar producer for this endpoint (compression, batching,
    ///     producer name, routing mode, etc.) immediately before it is created.
    /// </summary>
    internal Action<IProducerBuilder<ReadOnlySequence<byte>>>? ConfigureProducer { get; set; }

    /// <summary>
    ///     Optional Pulsar schema for this endpoint (GH-3183). When set, the producer and consumer are
    ///     created with this schema so the broker registers it for the topic (schema registration,
    ///     compatibility checks, evolution). The schema is a pass-through over the bytes Wolverine already
    ///     serializes, so the rest of the byte-oriented pipeline (mapper, CloudEvents, headers) is
    ///     unchanged. Null (the default) uses DotPulsar's raw <c>ByteSequence</c> schema (no registration).
    /// </summary>
    internal ISchema<ReadOnlySequence<byte>>? Schema { get; set; }

    /// <summary>
    ///     When true, this sending endpoint opts into Pulsar producer deduplication (GH-3185): the
    ///     producer is created with a stable <see cref="ProducerName"/> and stamps a monotonic per-message
    ///     sequence id, so the broker discards duplicate sends of the same message (e.g. outbox resends).
    ///     This is producer→broker dedup only, not end-to-end exactly-once, and requires broker
    ///     deduplication to be enabled on the namespace/topic. Set via <c>EnableDeduplication()</c>.
    /// </summary>
    internal bool DeduplicationEnabled { get; set; }

    /// <summary>
    ///     Stable Pulsar producer name used for deduplication. The broker tracks the last sequence id per
    ///     producer name, so this must be stable across producer sessions for dedup to span restarts. When
    ///     null, the sender derives one from the service name and topic.
    /// </summary>
    internal string? ProducerName { get; set; }

    /// <summary>
    ///     Optional message codec for schemas that own the body encoding (e.g. Avro, GH-3213). When set,
    ///     the sender encodes <c>envelope.Message</c> through the codec and the listener decodes back to the
    ///     message object directly (bypassing Wolverine's body serialization), while <see cref="Schema"/>
    ///     registers the matching schema with the broker. Null (the default) keeps the byte-oriented path
    ///     where Wolverine owns the body (raw bytes or the JSON pass-through schema).
    /// </summary>
    internal Schemas.IPulsarMessageCodec? MessageCodec { get; set; }

    /// <summary>
    ///     Use to override the dead letter topic for this endpoint
    /// </summary>
    public DeadLetterTopic? DeadLetterTopic { get; set; }

    /// <summary>
    ///     Use to override the retry letter topic for this endpoint
    /// </summary>
    public RetryLetterTopic? RetryLetterTopic { get; set; }

    /// <summary>
    /// The dead letter topic actually in effect for this endpoint: the per-endpoint
    /// <see cref="DeadLetterTopic"/> override if set, otherwise the transport-wide default
    /// (<see cref="PulsarTransport.DeadLetterTopic"/>). Per-endpoint configuration always wins.
    /// </summary>
    internal DeadLetterTopic? EffectiveDeadLetterTopic => DeadLetterTopic ?? _parent.DeadLetterTopic;

    /// <summary>
    /// The retry letter topic actually in effect for this endpoint: the per-endpoint
    /// <see cref="RetryLetterTopic"/> override if set, otherwise the transport-wide default
    /// (<see cref="PulsarTransport.RetryLetterTopic"/>). Per-endpoint configuration always wins.
    /// </summary>
    internal RetryLetterTopic? EffectiveRetryLetterTopic => RetryLetterTopic ?? _parent.RetryLetterTopic;

    /// <summary>
    /// Native dead-lettering routes failed messages to a real Pulsar topic, so report that
    /// (rather than the durable default) to monitoring when a native DLQ is in effect.
    /// </summary>
    public override DeadLetterStorageMode DeadLetterStorage =>
        EffectiveDeadLetterTopic is { Mode: DeadLetterTopicMode.Native }
            ? DeadLetterStorageMode.Native
            : DeadLetterStorageMode.Durable;

    public bool IsPersistent => Persistence.Equals(Persistent);

    /// <summary>
    /// Build a Pulsar-native topic-path URI of the form
    /// <c>persistent://{tenant}/{namespace}/{topic}</c> (or <c>non-persistent://...</c>) for
    /// hand-off to the native Pulsar client. This is NOT a Wolverine endpoint URI —
    /// for those, use <see cref="PulsarEndpointUri"/>.
    /// </summary>
    internal static Uri NativeTopicPath(bool persistent, string tenant, string @namespace, string topicName)
    {
        var scheme = persistent ? Persistent : NonPersistent;
        return new Uri($"{scheme}://{tenant}/{@namespace}/{topicName}");
    }

    public override IDictionary<string, object> DescribeProperties()
    {
        var dict = base.DescribeProperties();

        dict.Add(nameof(Persistent), Persistent);
        dict.Add(nameof(Tenant), Tenant);
        dict.Add(nameof(Namespace), Namespace);
        if (TopicName != null)
        {
            dict.Add(nameof(TopicName), TopicName);
        }

        return dict;
    }

    internal void Parse(Uri uri)
    {
        if (uri.Segments.Length != 4)
        {
            throw new InvalidPulsarUriException(uri);
        }

        if (uri.Host != Persistent && uri.Host != NonPersistent)
        {
            throw new InvalidPulsarUriException(uri);
        }

        Persistence = uri.Host;
        Tenant = uri.Segments[1].TrimEnd('/');
        Namespace = uri.Segments[2].TrimEnd('/');
        TopicName = uri.Segments[3].TrimEnd('/');
    }

    public string PulsarTopic()
    {
        return $"{Persistence}://{Tenant}/{Namespace}/{TopicName}";
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        // Hot-tail (GH-3184): a non-durable Reader at the tail rather than a durable subscription.
        if (IsHotTail)
        {
            var readerListener = new PulsarReaderListener(runtime, this, receiver, _parent, runtime.Cancellation);
            return ValueTask.FromResult<IListener>(readerListener);
        }

        var listener = new PulsarListener(runtime, this, receiver, _parent, runtime.Cancellation);
        return ValueTask.FromResult<IListener>(listener);
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return new PulsarSender(runtime, this, _parent, runtime.Cancellation);
    }

    public override bool TryBuildDeadLetterSender(IWolverineRuntime runtime, out ISender? deadLetterSender)
    {
        // Resolves the former DLQ-sender stub TODO. Pulsar dead-lettering is intentionally NOT done
        // through an endpoint-level sender:
        //  - Native mode is handled by PulsarListener (ISupportDeadLetterQueue), which produces to the
        //    {topic}-DLQ topic with the native reconsume metadata and retry-letter-topic chaining.
        //  - WolverineStorage mode is handled by the durable dead letter store.
        // Returning a sender here would make BufferedReceiver/DurableReceiver report
        // NativeDeadLetterQueueEnabled, which MessageContext.tryGetDeadLetterQueue prefers over the
        // listener — hijacking the richer native path and dropping the reconsume metadata. So we
        // defer to the base (no native endpoint sender). See #3186.
        return base.TryBuildDeadLetterSender(runtime, out deadLetterSender);
    }
}
