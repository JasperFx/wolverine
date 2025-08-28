using JasperFx.Core.Reflection;
using Newtonsoft.Json;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Configuration;

public class InteroperableSubscriberConfiguration<TSelf, TEndpoint, TMapper, TConcreteMapper> : SubscriberConfiguration<TSelf, TEndpoint>
    where TEndpoint : Endpoint<TMapper, TConcreteMapper>
    where TSelf : ISubscriberConfiguration<TSelf>
    where TConcreteMapper : IEnvelopeMapper, TMapper
{
    protected InteroperableSubscriberConfiguration(TEndpoint endpoint) : base(endpoint)
    {
    }

    /// <summary>
    /// Use a custom interoperability strategy to map Wolverine messages to an upstream
    /// system's protocol
    /// </summary>
    /// <param name="mapper"></param>
    /// <returns></returns>
    public TSelf UseInterop(TMapper mapper)
    {
        add(e => e.EnvelopeMapper = mapper);
        return this.As<TSelf>();
    }

    /// <summary>
    /// Customize the basic envelope mapping for interoperability. This mechanism
    /// is suitable if you are mostly needing to modify how headers are communicated
    /// from and to external systems through the underlying transport
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public TSelf UseInterop(Action<TEndpoint, TConcreteMapper> configure)
    {
        add(e => e.customizeMapping((m, _) => configure(e, m)));
        return this.As<TSelf>();
    }

    /// <summary>
    /// Create a completely customized mapper using the WolverineRuntime and the current
    /// Endpoint. This is built lazily at system bootstrapping time
    /// </summary>
    /// <param name="factory"></param>
    /// <returns></returns>
    public TSelf UseInterop(Func<IWolverineRuntime, TEndpoint, TMapper> factory)
    {
        add(e => e.registerMapperFactory(r => factory(r, e)));
        return this.As<TSelf>();
    }
}

/// <summary>
///     Base class for custom fluent interface expressions for external transport subscriber endpoints
/// </summary>
/// <typeparam name="T"></typeparam>
/// <typeparam name="TEndpoint"></typeparam>
public class SubscriberConfiguration<T, TEndpoint> : DelayedEndpointConfiguration<TEndpoint>,
    ISubscriberConfiguration<T>
    where TEndpoint : Endpoint where T : ISubscriberConfiguration<T>
{
    protected SubscriberConfiguration(TEndpoint endpoint) : base(endpoint)
    {
    }

    public T TelemetryEnabled(bool isEnabled)
    {
        add(e => e.TelemetryEnabled = isEnabled);
        return this.As<T>();
    }

    public T UseDurableOutbox()
    {
        add(e => e.Mode = EndpointMode.Durable);
        return this.As<T>();
    }

    public T BufferedInMemory()
    {
        add(e => e.Mode = EndpointMode.BufferedInMemory);
        return this.As<T>();
    }

    public T SendInline()
    {
        add(e => e.Mode = EndpointMode.Inline);
        return this.As<T>();
    }

    public T GlobalSender()
    {
        add(e => e.TenancyBehavior = TenancyBehavior.Global);
        return this.As<T>();
    }

    public T Named(string name)
    {
        add(e => e.EndpointName = name);
        return this.As<T>();
    }

    public T CustomNewtonsoftJsonSerialization(JsonSerializerSettings customSettings)
    {
        add(e =>
        {
            var serializer = new NewtonsoftSerializer(customSettings);
            e.RegisterSerializer(serializer);
            e.DefaultSerializer = serializer;
        });

        return this.As<T>();
    }

    /// <summary>
    /// For endpoints that send or receive messages in batches, this governs the maximum
    /// number of messages that will be received or sent in one batch
    /// </summary>
    public T MessageBatchSize(int batchSize)
    {
        add(e => e.MessageBatchSize = batchSize);
        return this.As<T>();
    }

    /// <summary>
    /// For endpoints that send messages in batches, this governs the maximum number
    /// of concurrent outgoing batches. The default is 1 to ensure message order, but increase this
    /// number to improve outgoing throughput
    /// </summary>
    public T MessageBatchMaxDegreeOfParallelism(int batchMaxDegreeOfParallelism)
    {
        add(e => e.MessageBatchMaxDegreeOfParallelism = batchMaxDegreeOfParallelism);
        return this.As<T>();
    }

    public T DefaultSerializer(IMessageSerializer serializer)
    {
        add(e =>
        {
            e.RegisterSerializer(serializer);
            e.DefaultSerializer = serializer;
        });

        return this.As<T>();
    }

    public T CustomizeOutgoing(Action<Envelope> customize)
    {
        add(e => e.OutgoingRules.Add(new LambdaEnvelopeRule(customize)));

        return this.As<T>();
    }

    public T CustomizeOutgoingMessagesOfType<TMessage>(Action<Envelope> customize)
    {
        add(e => e.OutgoingRules.Add(new LambdaEnvelopeRule<TMessage>(customize)));

        return this.As<T>();
    }

    public T CustomizeOutgoingMessagesOfType<TMessage>(Action<Envelope, TMessage> customize)
    {
        return CustomizeOutgoing(env =>
        {
            if (env.Message is TMessage message)
            {
                customize(env, message);
            }
        });
    }

    /// <summary>
    ///     Fine-tune the circuit breaker parameters for this outgoing subscriber endpoint
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public T CircuitBreaking(Action<ICircuitParameters> configure)
    {
        add(configure);
        return this.As<T>();
    }

    public T DeliverWithin(TimeSpan timeToLive)
    {
        add(e => e.OutgoingRules.Add(new DeliverWithinRule(timeToLive)));
        return this.As<T>();
    }

    /// <summary>
    ///     Add an outgoing envelope rule to modify how messages are sent from this
    ///     endpoint
    /// </summary>
    /// <param name="rule"></param>
    /// <returns></returns>
    public T AddOutgoingRule(IEnvelopeRule rule)
    {
        add(e => e.OutgoingRules.Add(rule));
        return this.As<T>();
    }
}

internal class SubscriberConfiguration : SubscriberConfiguration<ISubscriberConfiguration, Endpoint>,
    ISubscriberConfiguration
{
    public SubscriberConfiguration(Endpoint endpoint) : base(endpoint)
    {
    }
}