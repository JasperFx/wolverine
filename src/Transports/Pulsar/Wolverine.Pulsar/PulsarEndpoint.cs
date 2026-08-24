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
    ///     GH-4060. A hot-tail listener commits no cursor at all, so <c>PulsarReaderListener.DeferAsync</c> has
    ///     nothing to hand the message back to and is a deliberate no-op. Saying so here lets Wolverine warn at
    ///     bootstrap when the application configured requeue-shaped error handling that cannot possibly run.
    /// </summary>
    protected internal override bool supportsRedelivery => !IsHotTail;

    /// <summary>
    ///     When true, a requeue/defer of a single message uses Pulsar's native per-message
    ///     redelivery (<c>RedeliverUnacknowledgedMessages([messageId])</c>) — the message is left
    ///     unacknowledged and Pulsar redelivers that one message, preserving its redelivery count —
    ///     instead of the default behavior of acknowledging and re-publishing a fresh copy to the
    ///     source topic. Delayed/backoff redelivery is handled by the retry-letter topics (#3182).
    /// </summary>
    public bool UseNativeRedelivery { get; internal set; }

    /// <summary>
    ///     GH-4026. For a Durable listener: the most consumed messages the listener coalesces (for at most
    ///     5ms) into one batched inbox insert, instead of one insert round trip per message -- the same
    ///     micro-batching the RabbitMQ, Kafka and NATS listeners have. 1 reverts to strict
    ///     message-at-a-time persistence. Ignored for Buffered/Inline endpoints and for the retry-letter
    ///     consumer. Default 100.
    /// </summary>
    public int MaximumMessagesToReceive { get; set; } = 100;

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
    ///     GH-4047. How many messages the DotPulsar consumer prefetches into its client-side receiver queue
    ///     (<c>MessagePrefetchCount</c>). Null uses DotPulsar's own default of 1000, except under
    ///     <see cref="EndpointMode.NativeAck"/> -- see <see cref="EffectiveReceiverQueueSize"/>. Set through
    ///     <c>ReceiverQueueSize()</c>.
    /// </summary>
    public uint? ReceiverQueueSize { get; internal set; }

    /// <summary>
    ///     The receiver queue size actually handed to the consumer builder, or null to leave DotPulsar's default
    ///     of 1000 alone. An explicit <see cref="ReceiverQueueSize"/> always wins.
    ///
    ///     <para>
    ///     Under <see cref="EndpointMode.NativeAck"/> the receiver queue is Pulsar's nearest equivalent of
    ///     RabbitMQ's prefetch window (see <c>RabbitMqQueue.PreFetchCount</c>), and this mirrors that arm: it has
    ///     to cover every lane that can be busy at once -- the partition slot count when the endpoint is
    ///     group-partitioned, <see cref="Endpoint.MaxDegreeOfParallelism"/> otherwise -- doubled so a lane is never
    ///     starved waiting on the next delivery. It is NOT an unacked-message ceiling the way RabbitMQ's prefetch
    ///     is: Pulsar's flow-control permits are replenished as the receive loop drains the client queue, not as
    ///     messages are acked, so what actually bounds the in-memory execution block is the block's own bounded
    ///     capacity back-pressuring the receive loop. What this bounds is the buffer of prefetched-but-unstarted
    ///     deliveries, which under this mode is pure redelivery cost when the node dies -- and DotPulsar's default
    ///     of 1000 makes that cost roughly two orders of magnitude larger than it needs to be.
    ///     </para>
    /// </summary>
    internal uint? EffectiveReceiverQueueSize
    {
        get
        {
            if (ReceiverQueueSize.HasValue)
            {
                return ReceiverQueueSize;
            }

            if (Mode != EndpointMode.NativeAck)
            {
                return null;
            }

            var lanes = GroupShardingSlotNumber.HasValue
                ? Math.Max((int)GroupShardingSlotNumber.Value, MaxDegreeOfParallelism)
                : MaxDegreeOfParallelism;

            return (uint)Math.Max(lanes * 2, 1);
        }
    }

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

    /// <summary>
    /// GH-4047. Pulsar accepts <see cref="EndpointMode.NativeAck"/>. It qualifies on both counts the mode requires:
    /// a Pulsar consumer settles each delivery by its own <c>MessageId</c> -- <c>Acknowledge(messageId)</c>, and
    /// <c>RedeliverUnacknowledgedMessages([messageId])</c> for the negative case -- and the subscription cursor
    /// tracks individually acked messages in a set rather than a single offset, so settling out of delivery order
    /// leaves earlier unacked deliveries exactly where they were. That is what makes a completion-time ack from an
    /// arbitrary execution-block lane safe here and impossible on Kafka.
    ///
    /// <para>
    /// The qualification is conditional, and <see cref="validateModeConfiguration"/> enforces the condition:
    /// <b>the endpoint must not be acking cumulatively</b>. <see cref="PulsarAckStrategy.Cumulative"/> settles every
    /// message up to a point in the subscription with one ack, which under this mode -- where the execution block
    /// completes in handler-completion order, not delivery order -- is the same silent-loss defect that disqualifies
    /// Kafka and that GH-3706 fixed for RabbitMQ by making every ack <c>multiple: false</c>. Hot-tail listening is
    /// rejected for the related reason that a Pulsar <c>Reader</c> has no cursor to settle against at all.
    /// </para>
    /// </summary>
    protected override bool supportsNativeAck => true;

    /// <summary>
    /// GH-4047. The two ways a Pulsar endpoint can be configured into a state where native acks cannot deliver the
    /// no-loss guarantee. Both are checked after Compile() rather than in the <see cref="Endpoint.Mode"/> setter,
    /// because <c>AcknowledgeCumulative()</c> / <c>TailFromLatest()</c> and
    /// <c>ProcessInParallelWithNativeAcks()</c> are applied as delayed configuration in whatever order the fluent
    /// calls were written, and a check in the setter would only catch one of the two orderings.
    /// </summary>
    protected internal override IEnumerable<string> validateModeConfiguration()
    {
        if (Mode != EndpointMode.NativeAck)
        {
            yield break;
        }

        if (AckStrategy == PulsarAckStrategy.Cumulative)
        {
            yield return CumulativeAckIsIncompatibleWithNativeAck(this);
        }

        if (IsHotTail)
        {
            yield return
                $"Invalid listener configuration for Pulsar topic {PulsarTopic()}: TailFromLatest() cannot be combined " +
                $"with ProcessInParallelWithNativeAcks() (EndpointMode.{nameof(EndpointMode.NativeAck)}). Hot-tail " +
                "listening consumes through a non-durable Pulsar Reader, which has no subscription cursor -- there is " +
                "nothing to acknowledge, and nothing is ever redelivered, so the no-loss guarantee this mode exists " +
                "for cannot be made. Drop TailFromLatest(), or use BufferedInMemory() with it.";
        }
    }

    /// <summary>
    /// The one message both the bootstrap validation and the <c>PulsarListener</c> guard use, so the two can never
    /// drift into explaining the same refusal differently. Names BOTH settings, per GH-4047.
    /// </summary>
    internal static string CumulativeAckIsIncompatibleWithNativeAck(PulsarEndpoint endpoint)
    {
        return
            $"Invalid listener configuration for Pulsar topic {endpoint.PulsarTopic()}: AcknowledgeCumulative() " +
            $"(PulsarAckStrategy.{nameof(PulsarAckStrategy.Cumulative)}) cannot be combined with " +
            $"ProcessInParallelWithNativeAcks() (EndpointMode.{nameof(EndpointMode.NativeAck)}). A cumulative " +
            "acknowledgment settles every message up to a point in the subscription, and this mode completes " +
            "messages in handler-completion order rather than delivery order -- so acking a later message would " +
            "silently settle earlier deliveries that are still in flight, turning the mode's no-loss guarantee into " +
            "silent message loss. Use AcknowledgeIndividually() (the default) or AcknowledgeInBatches(), or choose " +
            "another endpoint mode.";
    }

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
        // Delegate to the transport so broker-per-tenant fan-out (GH-3308) and the hot-tail branch (GH-3184)
        // are resolved in one place. The transport builds a CompoundListener over per-tenant clusters when
        // tenants are registered and this endpoint is tenant-aware; otherwise a single listener.
        return _parent.BuildListenerAsync(this, receiver, runtime);
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        // Delegate to the transport so broker-per-tenant fan-out (GH-3308) is resolved in one place. The
        // transport wraps a TenantedSender over per-tenant clusters when tenants are registered and this
        // endpoint is tenant-aware; otherwise a single sender.
        return _parent.BuildSender(this, runtime);
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
