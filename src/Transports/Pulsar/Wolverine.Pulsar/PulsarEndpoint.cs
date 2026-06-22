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
    ///     When true, a requeue/defer of a single message uses Pulsar's native per-message
    ///     redelivery (<c>RedeliverUnacknowledgedMessages([messageId])</c>) — the message is left
    ///     unacknowledged and Pulsar redelivers that one message, preserving its redelivery count —
    ///     instead of the default behavior of acknowledging and re-publishing a fresh copy to the
    ///     source topic. Delayed/backoff redelivery is handled by the retry-letter topics (#3182).
    /// </summary>
    public bool UseNativeRedelivery { get; internal set; }

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
