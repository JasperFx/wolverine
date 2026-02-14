using Wolverine.ErrorHandling;

namespace Wolverine.Configuration;

public interface ISubscriberConfiguration<T> : IEndpointConfiguration<T> where T : ISubscriberConfiguration<T>
{
    /// <summary>
    ///     Force any messages enqueued to this worker queue to be durable by enrolling
    ///     outgoing messages in the active, durable envelope outbox
    /// </summary>
    /// <returns></returns>
    T UseDurableOutbox();

    /// <summary>
    ///     By default, messages on this worker queue will not be persisted until
    ///     being successfully handled
    /// </summary>
    /// <returns></returns>
    T BufferedInMemory();

    /// <summary>
    ///     Apply envelope customization rules to any outgoing
    ///     messages to this endpoint
    /// </summary>
    /// <param name="customize"></param>
    /// <returns></returns>
    T CustomizeOutgoing(Action<Envelope> customize);

    /// <summary>
    ///     Apply envelope customization rules to any outgoing
    ///     messages to this endpoint only for messages of either type
    ///     TMessage or types that implement or inherit from TMessage
    /// </summary>
    /// <param name="customize"></param>
    /// <returns></returns>
    T CustomizeOutgoingMessagesOfType<TMessage>(Action<Envelope> customize);

    /// <summary>
    ///     Apply envelope customization rules to any outgoing
    ///     messages to this endpoint only for messages of either type
    ///     TMessage or types that implement or inherit from TMessage
    /// </summary>
    /// <param name="customize"></param>
    /// <returns></returns>
    T CustomizeOutgoingMessagesOfType<TMessage>(Action<Envelope, TMessage> customize);

    /// <summary>
    ///     Limit all outgoing messages to a certain "deliver within" time span after which the messages
    ///     will be discarded even if not successfully delivered or processed
    /// </summary>
    /// <param name="timeToLive"></param>
    /// <returns></returns>
    T DeliverWithin(TimeSpan timeToLive);

    /// <summary>
    ///     Fine-tune the circuit breaker parameters for this outgoing subscriber endpoint
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    T CircuitBreaking(Action<ICircuitParameters> configure);

    /// <summary>
    ///     Outgoing messages are sent to the transport completely inline
    ///     in a predictable way with no retries
    /// </summary>
    /// <returns></returns>
    T SendInline();

    /// <summary>
    /// In the case of being part of tenancy aware group of message transports, this
    /// setting makes this listening endpoint a "global" endpoint that does not conform to the tenant-specific topology. 
    /// </summary>
    /// <returns></returns>
    public T GlobalSender();

    /// <summary>
    /// Configure failure handling policies for outgoing message send failures
    /// on this specific endpoint. These rules take priority over global policies.
    /// </summary>
    /// <param name="configure">Action to configure the sending failure policies</param>
    /// <returns></returns>
    T ConfigureSending(Action<SendingFailurePolicies> configure);

    public Endpoint Endpoint { get; }
}

public interface ISubscriberConfiguration : ISubscriberConfiguration<ISubscriberConfiguration>;