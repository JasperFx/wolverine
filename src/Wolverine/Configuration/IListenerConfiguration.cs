using System.Threading.Tasks.Dataflow;
using Newtonsoft.Json;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Configuration;

public interface IEndpointConfiguration<T>
{
    /// <summary>
    ///     Give this subscriber endpoint a diagnostic name. This will be used
    ///     by Open Telemetry diagnostics if set
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    T Named(string name);

    /// <summary>
    ///     Use custom Newtonsoft.Json settings for this listener endpoint
    /// </summary>
    /// <param name="customSettings"></param>
    /// <returns></returns>
    T CustomNewtonsoftJsonSerialization(JsonSerializerSettings customSettings);

    /// <summary>
    ///     Override the default serializer for this endpoint
    /// </summary>
    /// <param name="serializer"></param>
    /// <returns></returns>
    T DefaultSerializer(IMessageSerializer serializer);

    /// <summary>
    /// For endpoints that send or receive messages in batches, this governs the maximum
    /// number of messages that will be received or sent in one batch
    /// </summary>
    T MessageBatchSize(int batchSize);

    /// <summary>
    /// Toggle whether or not Open Telemetry auditing is enabled or disabled for sending, receiving, or executing messages received
    /// at this endpoint
    /// </summary>
    /// <param name="isEnabled"></param>
    /// <returns></returns>
    T TelemetryEnabled(bool isEnabled);
}

public interface IListenerConfiguration<T> : IEndpointConfiguration<T>
{
    /// <summary>
    /// If supported, this will make Wolverine listen to this endpoint on
    /// only one active node and with an inline listener or a sequential, buffered listener
    /// for endpoints that do not support inline listeners
    /// </summary>
    /// <param name="endpointName">Wolverine requires a unique endpoint name for this usage. Most endpoints will supply one automatically, usually based on a queue or topic name, but some endpoint types may require an explicit name</param>
    /// <returns></returns>
    T ListenWithStrictOrdering(string? endpointName = null);

    /// <summary>
    ///     Specify the maximum number of threads that this worker queue
    ///     can use at one time
    /// </summary>
    /// <param name="maximumParallelHandlers"></param>
    /// <returns></returns>
    T MaximumParallelMessages(int maximumParallelHandlers);

    /// <summary>
    ///     Forces this worker queue to use no more than one thread
    /// </summary>
    /// <returns></returns>
    T Sequential();

    /// <summary>
    ///     Force any messages enqueued to this worker queue to be durable
    /// </summary>
    /// <returns></returns>
    T UseDurableInbox();

    /// <summary>
    ///     Force any messages enqueued to this worker queue to be durable
    /// </summary>
    /// <returns></returns>
    T UseDurableInbox(BufferingLimits limits);

    /// <summary>
    ///     Incoming messages are immediately moved into an in-memory queue
    ///     for parallel processing
    /// </summary>
    /// <returns></returns>
    T BufferedInMemory();

    /// <summary>
    ///     Incoming messages are immediately moved into an in-memory queue
    ///     for parallel processing
    /// </summary>
    /// <returns></returns>
    T BufferedInMemory(BufferingLimits limits);


    /// <summary>
    ///     Incoming messages are executed in
    /// </summary>
    /// <returns></returns>
    T ProcessInline();

    /// <summary>
    ///     Mark this listener as the preferred endpoint for replies from other systems
    /// </summary>
    /// <returns></returns>
    T UseForReplies();

    /// <summary>
    /// Direct Wolverine to use the specified handler type for its messages on
    /// only this listening endpoint. This is helpful to create "sticky" handlers for the
    /// same message type on multiple queues
    /// </summary>
    /// <param name="handlerType"></param>
    /// <returns></returns>
    T AddStickyHandler(Type handlerType);
}

public interface IListenerConfiguration : IListenerConfiguration<IListenerConfiguration>;