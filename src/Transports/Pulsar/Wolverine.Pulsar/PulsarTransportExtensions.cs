using System.Buffers;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotPulsar;
using DotPulsar.Abstractions;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Pulsar.ErrorHandling;
using Wolverine.Pulsar.Internal;
using Wolverine.Pulsar.Schemas;
using Wolverine.Runtime.Partitioning;

namespace Wolverine.Pulsar;

public static class PulsarTransportExtensions
{
    /// <summary>
    ///     Quick access to the Pulsar Transport within this application.
    ///     This is for advanced usage
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    internal static PulsarTransport PulsarTransport(this WolverineOptions endpoints)
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        return transports.GetOrCreate<PulsarTransport>();
    }

    /// <summary>
    ///     Configure connection and authentication information about the Pulsar usage
    ///     within this Wolverine application
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="configure"></param>
    public static PulsarConfiguration UsePulsar(this WolverineOptions endpoints, Action<IPulsarClientBuilder> configure)
    {
        new PulsarNativeResiliencyPolicy().Apply(endpoints);

        var transport = endpoints.PulsarTransport();
        configure(transport.Builder);

        return new PulsarConfiguration(transport);
    }

    /// <summary>
    ///     Connect to a local, standalone Pulsar broker at the default port
    /// </summary>
    /// <param name="endpoints"></param>
    public static PulsarConfiguration UsePulsar(this WolverineOptions endpoints)
    {
        return endpoints.UsePulsar(_ => { });
    }

    /// <summary>
    ///     Publish matching messages to Pulsar using the named routing key or queue name and
    ///     optionally an exchange
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="topicPath">Pulsar topic of the form "persistent|non-persistent://tenant/namespace/topic"</param>
    /// <returns></returns>
    public static PulsarSubscriberConfiguration ToPulsarTopic(this IPublishToExpression publishing, string topicPath)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<PulsarTransport>();
        var endpoint = transport.EndpointFor(topicPath);

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(endpoint.Uri);

        return new PulsarSubscriberConfiguration(endpoint);
    }

    /// <summary>
    ///     Listen to a specified Pulsar topic path of the path "persistent|non-persistent://tenant/namespace/topic"
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="topicPath"></param>
    /// <returns></returns>
    public static PulsarListenerConfiguration ListenToPulsarTopic(this WolverineOptions endpoints, string topicPath)
    {
        var uri = PulsarEndpointUri.Topic(topicPath);
        var endpoint = endpoints.PulsarTransport()[uri];
        endpoint.IsListener = true;
        return new PulsarListenerConfiguration(endpoint);
    }

    /// <summary>
    ///     Set the specified unsubscribe on close setting for all Pulsar endpoints.
    /// </summary>
    /// <param name="options"></param>
    /// <param name="unsubscribeOnClose"></param>
    /// <returns></returns>
    public static IPolicies UnsubscribePulsarOnClose(this IPolicies policies, PulsarUnsubscribeOnClose unsubscribeOnClose)
    {
        policies.Add(new PulsarUnsubscribeOnClosePolicy(unsubscribeOnClose));
        return policies;
    }

    /// <summary>
    ///     Disable the possibility of requeueing messages for all Pulsar endpoints.
    /// </summary>
    /// <param name="options"></param>
    /// <returns></returns>
    public static IPolicies DisablePulsarRequeue(this IPolicies policies)
    {
        policies.Add(new PulsarEnableRequeuePolicy(PulsarRequeue.Disabled));
        return policies;
    }

    /// <summary>
    ///     Apply CloudEvents interop to all Pulsar endpoints. This configures both
    ///     listening and sending endpoints to use the CloudEvents message format.
    /// </summary>
    /// <param name="policies"></param>
    /// <param name="jsonSerializerOptions">Optional JSON serializer options for CloudEvents serialization</param>
    /// <returns></returns>
    public static IPolicies UsePulsarWithCloudEvents(this IPolicies policies, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        policies.Add(new PulsarCloudEventsPolicy(jsonSerializerOptions));
        return policies;
    }

    /// <summary>
    /// Create a sharded message topology with Pulsar topics named
    /// baseName1, baseName2, etc.
    /// </summary>
    /// <param name="rules"></param>
    /// <param name="baseName"></param>
    /// <param name="numberOfEndpoints"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static MessagePartitioningRules PublishToShardedPulsarTopics(this MessagePartitioningRules rules, string baseName, int numberOfEndpoints, Action<PartitionedMessageTopologyWithTopics> configure)
    {
        rules.AddPublishingTopology((opts, _) =>
        {
            var topology = new PartitionedMessageTopologyWithTopics(opts, PartitionSlots.Five, baseName, numberOfEndpoints);
            topology.ConfigureListening(x => {});
            configure(topology);
            topology.AssertValidity();

            return topology;
        });

        return rules;
    }

    /// <summary>
    /// Use sharded Pulsar topics for global partitioned message processing.
    /// Topics will be named baseName1, baseName2, etc.
    /// </summary>
    /// <param name="topology"></param>
    /// <param name="baseName"></param>
    /// <param name="numberOfEndpoints"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static GlobalPartitionedMessageTopology UseShardedPulsarTopics(
        this GlobalPartitionedMessageTopology topology, string baseName, int numberOfEndpoints,
        Action<PartitionedMessageTopologyWithTopics>? configure = null)
    {
        topology.SetExternalTopology(opts =>
        {
            var t = new PartitionedMessageTopologyWithTopics(opts, PartitionSlots.Five, baseName, numberOfEndpoints);
            t.ConfigureListening(x => {});
            configure?.Invoke(t);
            return t;
        }, baseName);
        return topology;
    }
}

public class PulsarListenerConfiguration : InteroperableListenerConfiguration<PulsarListenerConfiguration, PulsarEndpoint, IPulsarEnvelopeMapper, PulsarEnvelopeMapper>
{
    public PulsarListenerConfiguration(PulsarEndpoint endpoint) : base(endpoint)
    {
    }

    /// <summary>
    /// Provide a subscription name to Pulsar for this topic
    /// </summary>
    /// <param name="subscriptionName"></param>
    /// <returns></returns>
    public PulsarListenerConfiguration SubscriptionName(string subscriptionName)
    {
        add(e =>
        {
            e.SubscriptionName = subscriptionName;
        });

        return this;
    }

    /// <summary>
    /// Override the Pulsar subscription type for just this topic
    /// </summary>
    /// <param name="subscriptionType"></param>
    /// <returns></returns>
    public PulsarListenerConfiguration SubscriptionType(SubscriptionType subscriptionType)
    {
        add(e =>
        {
            e.SubscriptionType = subscriptionType;
        });

        // TODO: check how to restrict it properly
        //if (subscriptionType is DotPulsar.SubscriptionType.Shared or DotPulsar.SubscriptionType.KeyShared)
        //    return new PulsarSharedListenerConfiguration(this._endpoint);

        return this;
    }

    /// <summary>
    /// Set where a brand-new subscription starts consuming. Only affects the first read of a
    /// not-yet-existing subscription; once the subscription exists, Pulsar resumes from its
    /// committed position regardless of this setting.
    /// </summary>
    /// <param name="initialPosition"></param>
    /// <returns></returns>
    public PulsarListenerConfiguration SubscriptionInitialPosition(SubscriptionInitialPosition initialPosition)
    {
        add(e => { e.SubscriptionInitialPosition = initialPosition; });
        return this;
    }

    /// <summary>
    /// Start a brand-new subscription at the earliest retained message in the topic (replay the
    /// existing backlog). Pulsar analogue of the Kafka transport's <c>BeginAtEarliest()</c>. Only
    /// applies on the first read of a not-yet-existing subscription.
    /// </summary>
    /// <returns></returns>
    public PulsarListenerConfiguration BeginAtEarliest()
    {
        add(e => { e.SubscriptionInitialPosition = DotPulsar.SubscriptionInitialPosition.Earliest; });
        return this;
    }

    /// <summary>
    /// Start a brand-new subscription at the latest position so only messages published after the
    /// subscription is created are consumed (DotPulsar's default). Pulsar analogue of the Kafka
    /// transport's <c>BeginAtLatest()</c>. Only applies on the first read of a not-yet-existing
    /// subscription.
    /// </summary>
    /// <returns></returns>
    public PulsarListenerConfiguration BeginAtLatest()
    {
        add(e => { e.SubscriptionInitialPosition = DotPulsar.SubscriptionInitialPosition.Latest; });
        return this;
    }

    /// <summary>
    /// Ephemeral "hot-tail" / broadcast consume (GH-3184): this listener uses a non-durable Pulsar
    /// <c>Reader</c> cursor starting at the tail (<see cref="DotPulsar.MessageId.Latest"/>) instead of a
    /// durable subscription, so every node receives all messages published after it joins and never
    /// replays history — the idiomatic Pulsar pattern for live dashboards and fan-out-to-all. The reader
    /// cursor is throwaway and unacknowledged, so dead-letter / retry-letter queueing, native redelivery,
    /// and acknowledgment strategies do not apply. Pulsar analogue of the Kafka transport's
    /// <c>TailFromLatest()</c>.
    /// </summary>
    /// <returns></returns>
    public PulsarListenerConfiguration TailFromLatest()
    {
        add(e =>
        {
            e.IsHotTail = true;
            e.SubscriptionInitialPosition = DotPulsar.SubscriptionInitialPosition.Latest;
        });
        return this;
    }

    /// <summary>
    /// Have this single listener consume from one or more additional Pulsar topics alongside the
    /// primary topic it was created with. Pulsar supports a single consumer over multiple topics;
    /// this is the analogue of Kafka topic groups. Each value is a full native topic path, e.g.
    /// <c>persistent://public/default/other</c>.
    /// </summary>
    /// <param name="additionalTopicPaths"></param>
    /// <returns></returns>
    public PulsarListenerConfiguration Topics(params string[] additionalTopicPaths)
    {
        // Validate eagerly so a malformed topic path fails at configuration time with a clear error.
        foreach (var path in additionalTopicPaths)
        {
            _ = PulsarEndpointUri.Topic(path);
        }

        add(e =>
        {
            foreach (var path in additionalTopicPaths)
            {
                if (path != e.PulsarTopic() && !e.AdditionalTopics.Contains(path))
                {
                    e.AdditionalTopics.Add(path);
                }
            }
        });
        return this;
    }

    /// <summary>
    /// Subscribe this listener to every topic matching <paramref name="pattern"/> (Pulsar pattern
    /// subscription) rather than an explicit topic or topic list. The topic the listener was created
    /// with is used only as the Wolverine endpoint identity.
    /// </summary>
    /// <param name="pattern">Regex matched against native topic paths within the namespace.</param>
    /// <param name="mode">Which topics the pattern matches (persistent / non-persistent / all).</param>
    /// <returns></returns>
    public PulsarListenerConfiguration TopicsPattern(Regex pattern,
        RegexSubscriptionMode mode = RegexSubscriptionMode.Persistent)
    {
        add(e =>
        {
            e.TopicsPattern = pattern;
            e.RegexSubscriptionMode = mode;
        });
        return this;
    }

    /// <summary>
    /// Requeue/defer a failed message using Pulsar's native per-message redelivery
    /// (<c>RedeliverUnacknowledgedMessages</c>) instead of acknowledging it and re-publishing a
    /// fresh copy to the source topic. The original message is left unacknowledged and Pulsar
    /// redelivers just that one message, preserving its redelivery count. For delayed/backoff
    /// redelivery use the retry-letter topics instead.
    /// </summary>
    /// <returns></returns>
    public PulsarListenerConfiguration UseNativeRedelivery()
    {
        add(e => { e.UseNativeRedelivery = true; });
        return this;
    }

    /// <summary>
    /// Register a Pulsar JSON schema for <typeparamref name="T"/> on this listener (GH-3183). The broker
    /// registers the topic's JSON schema (enabling schema compatibility checks + evolution); the message
    /// body remains Wolverine's normal JSON serialization. The producing endpoint must register a
    /// compatible schema.
    /// </summary>
    public PulsarListenerConfiguration UseJsonSchema<T>()
    {
        add(e => e.Schema = PulsarSchema.ForJson(typeof(T)));
        return this;
    }

    /// <summary>
    /// Register a Pulsar Avro schema for <typeparamref name="T"/> on this listener (GH-3213). The broker
    /// registers the topic's Avro schema and the body is genuine Avro on the wire (DotPulsar's built-in
    /// <c>Schema.AvroISpecificRecord&lt;T&gt;()</c>). <typeparamref name="T"/> must be an Apache.Avro
    /// <c>ISpecificRecord</c>. The producing endpoint must register a compatible schema.
    /// </summary>
    public PulsarListenerConfiguration UseAvroSchema<T>() where T : class
    {
        add(e =>
        {
            var codec = new PulsarAvroCodec<T>();
            e.MessageCodec = codec;
            e.Schema = new PulsarSchema(codec.SchemaInfo);
        });
        return this;
    }

    /// <summary>
    /// Register a custom Pulsar schema on this listener (GH-3183). The schema's <c>SchemaInfo</c> is what
    /// the broker stores for the topic; <c>Encode</c>/<c>Decode</c> operate over the bytes Wolverine
    /// serializes, so use a pass-through schema unless you are also replacing Wolverine's body serializer.
    /// </summary>
    public PulsarListenerConfiguration UsePulsarSchema(ISchema<ReadOnlySequence<byte>> schema)
    {
        add(e => e.Schema = schema);
        return this;
    }

    /// <summary>
    /// Customize the DotPulsar consumer for this listener (consumer name, receive-queue size,
    /// priority level, read-compacted, properties, etc.) immediately before it is created. Runs
    /// against the same <see cref="IConsumerBuilder{TMessage}"/> Wolverine uses internally.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PulsarListenerConfiguration ConfigureConsumer(Action<IConsumerBuilder<ReadOnlySequence<byte>>> configure)
    {
        add(e => { e.ConfigureConsumer = configure; });
        return this;
    }

    /// <summary>
    /// Customize the DotPulsar producer used by this listener for requeue/redelivery sends
    /// immediately before it is created.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PulsarListenerConfiguration ConfigureProducer(Action<IProducerBuilder<ReadOnlySequence<byte>>> configure)
    {
        add(e => { e.ConfigureProducer = configure; });
        return this;
    }

    /// <summary>
    /// Acknowledge each message individually as it completes (the default).
    /// </summary>
    /// <returns></returns>
    public PulsarListenerConfiguration AcknowledgeIndividually()
    {
        add(e => { e.AckStrategy = PulsarAckStrategy.Individual; });
        return this;
    }

    /// <summary>
    /// Acknowledge cumulatively — one ack confirms every message up to a point in the subscription,
    /// reducing broker chatter on high-volume ordered subscriptions. Only valid for Exclusive /
    /// Failover subscriptions (a clear error is thrown at startup otherwise). Wolverine only advances
    /// the cumulative ack to the highest contiguous-completed message, so it never acks a message
    /// that is still being processed.
    /// </summary>
    /// <returns></returns>
    public PulsarListenerConfiguration AcknowledgeCumulative()
    {
        add(e => { e.AckStrategy = PulsarAckStrategy.Cumulative; });
        return this;
    }

    /// <summary>
    /// Acknowledge messages individually but in batches, flushed when <paramref name="batchSize"/>
    /// messages have completed or every <paramref name="interval"/> (whichever comes first). Reduces
    /// broker chatter without the ordering constraints of cumulative ack; safe for every subscription
    /// type.
    /// </summary>
    /// <param name="batchSize">Flush after this many completed messages. Default 100.</param>
    /// <param name="interval">Also flush at least this often. Default 1 second; pass
    /// <see cref="TimeSpan.Zero"/> to flush only by count.</param>
    /// <returns></returns>
    public PulsarListenerConfiguration AcknowledgeInBatches(int batchSize = 100, TimeSpan? interval = null)
    {
        add(e =>
        {
            e.AckStrategy = PulsarAckStrategy.Batched;
            e.AckBatchSize = batchSize;
            e.AckBatchInterval = interval ?? TimeSpan.FromSeconds(1);
        });
        return this;
    }

    /// <summary>
    /// Override the Pulsar subscription type to  <see cref="DotPulsar.SubscriptionType.Failover"/> for just this topic
    /// </summary>
    /// <param name="subscriptionType"></param>
    /// <returns></returns>
    public PulsarListenerConfiguration WithFailoverSubscriptionType()
    {
        add(e => { e.SubscriptionType = DotPulsar.SubscriptionType.Failover; });

        return this;
    }

    /// <summary>
    /// Override the Pulsar subscription type to  <see cref="DotPulsar.SubscriptionType.Exclusive"/> for just this topic
    /// </summary>
    /// <param name="subscriptionType"></param>
    /// <returns></returns>
    public PulsarListenerConfiguration WithExclusiveSubscriptionType()
    {
        add(e => { e.SubscriptionType = DotPulsar.SubscriptionType.Exclusive; });

        return this;
    }

    /// <summary>
    /// Override the Pulsar subscription type to <see cref="DotPulsar.SubscriptionType.Shared"/> for just this topic
    /// </summary>
    /// <param name="subscriptionType"></param>
    /// <returns></returns>
    public PulsarNativeResiliencyDeadLetterConfiguration WithSharedSubscriptionType()
    {
        add(e => { e.SubscriptionType = DotPulsar.SubscriptionType.Shared; });

        return new PulsarNativeResiliencyDeadLetterConfiguration(new PulsarListenerConfiguration(_endpoint!));
    }


    /// <summary>
    /// Override the Pulsar subscription type to <see cref="DotPulsar.SubscriptionType.KeyShared"/> for just this topic
    /// </summary>
    /// <param name="subscriptionType"></param>
    /// <returns></returns>
    public PulsarNativeResiliencyDeadLetterConfiguration WithKeySharedSubscriptionType()
    {
        add(e => { e.SubscriptionType = DotPulsar.SubscriptionType.KeyShared; });

        return new PulsarNativeResiliencyDeadLetterConfiguration(new PulsarListenerConfiguration(_endpoint!));
    }

    /// <summary>
    ///     Add circuit breaker exception handling to this listener
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PulsarListenerConfiguration CircuitBreaker(Action<CircuitBreakerOptions>? configure = null)
    {
        add(e =>
        {
            e.CircuitBreakerOptions = new CircuitBreakerOptions();
            configure?.Invoke(e.CircuitBreakerOptions);
        });


        return this;
    }

    /// <summary>
    ///     Disable the possibility of requeueing messages
    /// </summary>
    /// <returns></returns>
    public PulsarListenerConfiguration DisableRequeue()
    {
        add(e =>
        {
            e.EnableRequeue = false;
        });

        return this;
    }
    
        /// <summary>
    /// Customize the dead letter queueing for this specific endpoint
    /// </summary>
    /// <param name="configure">Optional configuration</param>
    /// <returns></returns>
    public PulsarListenerConfiguration DeadLetterQueueing(DeadLetterTopic dlq)
    {
        add(e =>
        {
            e.DeadLetterTopic = dlq;
            e.Runtime!.Options.Policies.OnAnyException().MoveToErrorQueue();
        });

        return this;
    }

    /// <summary>
    ///     Set whether the subscription should be unsubscribed when the listener is closed.
    /// </summary>
    /// <param name="unsubscribeOnClose"></param>
    /// <returns></returns>
    public PulsarListenerConfiguration UnsubscribeOnClose(bool unsubscribeOnClose)
    {
        add(e =>
        {
            e.UnsubscribeOnClose = unsubscribeOnClose;
        });

        return this;
    }
    
    internal void Apply(Action<PulsarEndpoint> action)
    {
        add(action);
    }

    // /// <summary>
    // /// To optimize the message listener throughput,
    // /// start up multiple listening endpoints. This is
    // /// most necessary when using inline processing
    // /// </summary>
    // /// <param name="count"></param>
    // /// <returns></returns>
    // public PulsarListenerConfiguration ListenerCount(int count)
    // {
    //     if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), "Must be greater than zero");
    //
    //     endpoint.ListenerCount = count;
    //     return this;
    // }
}

public class PulsarNativeResiliencyConfig
{
    public DeadLetterTopic? DeadLetterTopic { get; set; }
    public RetryLetterTopic? RetryLetterTopic { get; set; }


    public Action<PulsarEndpoint> Apply()
    {
        return endpoint =>
        {
            if (RetryLetterTopic is null && DeadLetterTopic is null)
            {
                endpoint.DeadLetterTopic = null;
                endpoint.RetryLetterTopic = null;
                return;
            }

            // Set the DLQ configuration regardless
            if (DeadLetterTopic is not null)
            {
                endpoint.DeadLetterTopic = DeadLetterTopic;
            }

            if (RetryLetterTopic is not null)
            {
                // Validate subscription type
                if (endpoint.SubscriptionType is SubscriptionType.Failover or SubscriptionType.Exclusive)
                {
                    throw new InvalidOperationException(
                        "Pulsar does not support Retry letter queueing with Failover or Exclusive subscription types. Please use Shared or KeyShared subscription types.");
                }

                // Set retry configuration
                endpoint.RetryLetterTopic = RetryLetterTopic;

                if (endpoint.Runtime?.Options != null)
                {
                    endpoint.Runtime.Options.EnableAutomaticFailureAcks = false;
                }
            }
        };
    }

}

public abstract class PulsarNativeResiliencyConfiguration
{
    protected readonly PulsarListenerConfiguration Endpoint;
    protected PulsarNativeResiliencyConfig NativeResiliencyConfig;

    protected PulsarNativeResiliencyConfiguration(PulsarListenerConfiguration endpoint)
    {
        Endpoint = endpoint;
        NativeResiliencyConfig = new PulsarNativeResiliencyConfig();

    } 

    protected PulsarNativeResiliencyConfiguration(PulsarListenerConfiguration endpoint, PulsarNativeResiliencyConfig config)
    {
        Endpoint = endpoint;
        NativeResiliencyConfig = config;

    }

}


public class PulsarNativeResiliencyDeadLetterConfiguration : PulsarNativeResiliencyConfiguration
{


    public PulsarNativeResiliencyDeadLetterConfiguration(PulsarListenerConfiguration endpoint)
        : base(endpoint)
    {


    }

    /// <summary>
    /// Customize the dead letter queueing for this specific endpoint
    /// </summary>
    /// <param name="dlq">DLQ configuration</param>
    /// <returns></returns>
    public PulsarNativeResiliencyRetryLetterConfiguration DeadLetterQueueing(DeadLetterTopic dlq)
    {
        NativeResiliencyConfig.DeadLetterTopic = dlq;

        return new PulsarNativeResiliencyRetryLetterConfiguration(Endpoint, NativeResiliencyConfig);
    }

    /// <summary>
    /// Disable native DLQ functionality for this queue
    /// </summary>
    /// <returns></returns>
    public PulsarListenerConfiguration DisableDeadLetterQueueing()
    {
        return this.Endpoint;
    }
}

public class PulsarNativeResiliencyRetryLetterConfiguration : PulsarNativeResiliencyConfiguration
{

    public PulsarNativeResiliencyRetryLetterConfiguration(PulsarListenerConfiguration endpoint, PulsarNativeResiliencyConfig config)
        : base(endpoint, config)
    {


    }

    /// <summary>
    /// Customize the retry letter queueing for this specific endpoint
    /// </summary>
    /// <param name="configure">Optional configuration</param>
    /// <returns></returns>
    public PulsarListenerConfiguration RetryLetterQueueing(RetryLetterTopic rt)
    {
        NativeResiliencyConfig.RetryLetterTopic = rt;
        Endpoint.Apply(NativeResiliencyConfig.Apply());

        return Endpoint;
    }

    /// <summary>
    /// Disable native Retry letter functionality for this queue
    /// </summary>
    /// <returns></returns>
    public PulsarListenerConfiguration DisableRetryLetterQueueing()
    {
        NativeResiliencyConfig.RetryLetterTopic = null;
        Endpoint.Apply(NativeResiliencyConfig.Apply());

        return Endpoint;
    }
}

public class PulsarSubscriberConfiguration : InteroperableSubscriberConfiguration<PulsarSubscriberConfiguration, PulsarEndpoint, IPulsarEnvelopeMapper, PulsarEnvelopeMapper>
{
    public PulsarSubscriberConfiguration(PulsarEndpoint endpoint) : base(endpoint)
    {
    }

    /// <summary>
    /// Customize the DotPulsar producer for this sending endpoint (compression, batching, producer
    /// name, routing mode, etc.) immediately before it is created. Runs against the same
    /// <see cref="IProducerBuilder{TMessage}"/> Wolverine uses internally.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public PulsarSubscriberConfiguration ConfigureProducer(Action<IProducerBuilder<ReadOnlySequence<byte>>> configure)
    {
        add(e => { e.ConfigureProducer = configure; });
        return this;
    }

    /// <summary>
    /// Register a Pulsar JSON schema for <typeparamref name="T"/> on this sending endpoint (GH-3183). The
    /// broker registers the topic's JSON schema; the message body remains Wolverine's normal JSON
    /// serialization. The consuming endpoint must register a compatible schema.
    /// </summary>
    public PulsarSubscriberConfiguration UseJsonSchema<T>()
    {
        add(e => e.Schema = PulsarSchema.ForJson(typeof(T)));
        return this;
    }

    /// <summary>
    /// Register a Pulsar Avro schema for <typeparamref name="T"/> on this sending endpoint (GH-3213). The
    /// broker registers the topic's Avro schema and the body is genuine Avro on the wire (DotPulsar's
    /// built-in <c>Schema.AvroISpecificRecord&lt;T&gt;()</c>). <typeparamref name="T"/> must be an
    /// Apache.Avro <c>ISpecificRecord</c>. The consuming endpoint must register a compatible schema.
    /// </summary>
    public PulsarSubscriberConfiguration UseAvroSchema<T>() where T : class
    {
        add(e =>
        {
            var codec = new PulsarAvroCodec<T>();
            e.MessageCodec = codec;
            e.Schema = new PulsarSchema(codec.SchemaInfo);
        });
        return this;
    }

    /// <summary>
    /// Register a custom Pulsar schema on this sending endpoint (GH-3183). See
    /// <see cref="PulsarListenerConfiguration.UsePulsarSchema"/>.
    /// </summary>
    public PulsarSubscriberConfiguration UsePulsarSchema(ISchema<ReadOnlySequence<byte>> schema)
    {
        add(e => e.Schema = schema);
        return this;
    }

    /// <summary>
    /// Enable Pulsar producer deduplication for this sending endpoint (GH-3185). The producer is created
    /// with a stable producer name and stamps a monotonic per-message sequence id, so the broker discards
    /// duplicate sends of the same message (for example outbox resends of the same envelope).
    ///
    /// This is **producer→broker** deduplication only, not end-to-end exactly-once, and it requires broker
    /// deduplication to be enabled on the namespace/topic (e.g. <c>pulsar-admin namespaces
    /// set-deduplication public/default --enable</c>).
    /// </summary>
    /// <param name="producerName">
    /// Optional stable producer name. The broker tracks the last sequence id per producer name, so a fixed
    /// name lets dedup span producer restarts. When omitted, a name is derived from the service name and
    /// topic.
    /// </param>
    public PulsarSubscriberConfiguration EnableDeduplication(string? producerName = null)
    {
        add(e =>
        {
            e.DeduplicationEnabled = true;
            if (producerName != null)
            {
                e.ProducerName = producerName;
            }
        });
        return this;
    }
}
