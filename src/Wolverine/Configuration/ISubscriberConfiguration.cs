using System;
using Newtonsoft.Json;
using Wolverine.Runtime.Serialization;

namespace Wolverine.Configuration;

public interface ISubscriberConfiguration<T> where T : ISubscriberConfiguration<T>
{
    /// <summary>
    /// Force any messages enqueued to this worker queue to be durable by enrolling
    /// outgoing messages in the active, durable envelope outbox
    /// </summary>
    /// <returns></returns>
    T UseDurableOutbox();

    /// <summary>
    /// Force any messages enqueued to this worker queue to be durable by enrolling
    /// outgoing messages in the active, durable envelope outbox
    /// </summary>
    /// <returns></returns>
    [Obsolete("Switch to UseDurableOutbox(). Will be removed in Wolverine 2.0")]
    T UsePersistentOutbox();

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
    ///     Give this subscriber endpoint a diagnostic name. This will be used
    ///     by Open Telemetry diagnostics if set
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    T Named(string name);

    /// <summary>
    ///     Use custom Newtonsoft.Json settings for "application/json" serialization in this
    ///     endpoint
    /// </summary>
    /// <param name="customSettings"></param>
    /// <returns></returns>
    ISubscriberConfiguration<T> CustomNewtonsoftJsonSerialization(JsonSerializerSettings customSettings);

    /// <summary>
    ///     Override the default serialization for only this subscriber endpoint
    /// </summary>
    /// <param name="serializer"></param>
    /// <returns></returns>
    ISubscriberConfiguration<T> DefaultSerializer(IMessageSerializer serializer);
}

public interface ISubscriberConfiguration : ISubscriberConfiguration<ISubscriberConfiguration>
{
}
