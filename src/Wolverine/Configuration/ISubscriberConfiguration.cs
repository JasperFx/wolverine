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
}

public interface ISubscriberConfiguration : ISubscriberConfiguration<ISubscriberConfiguration>;