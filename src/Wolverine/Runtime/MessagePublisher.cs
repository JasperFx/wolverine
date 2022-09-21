using System;
using System.Linq;
using System.Threading.Tasks;
using Baseline;
using Lamar;

namespace Wolverine.Runtime;

public class MessagePublisher : CommandBus, IMessagePublisher
{
    [DefaultConstructor]
    public MessagePublisher(IWolverineRuntime runtime) : base(runtime)
    {
    }

    public MessagePublisher(IWolverineRuntime runtime, string? correlationId) : base(runtime, correlationId)
    {
    }

    public ValueTask SendAsync<T>(T message, DeliveryOptions? options = null)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        // Cannot trust the T here. Can be "object"
        var outgoing = Runtime.RoutingFor(message.GetType()).RouteForSend(message, options);
        trackEnvelopeCorrelation(outgoing);

        return persistOrSendAsync(outgoing);
    }

    public ValueTask PublishAsync<T>(T message, DeliveryOptions? options = null)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        // TODO -- eliminate this. Only happening for logging at this point. Check same in Send.
        var envelope = new Envelope(message);

        // You can't trust the T here.
        var outgoing = Runtime.RoutingFor(message.GetType()).RouteForPublish(message, options);
        trackEnvelopeCorrelation(outgoing);

        if (outgoing.Any())
        {
            return persistOrSendAsync(outgoing);
        }

        Runtime.MessageLogger.NoRoutesFor(envelope);
        return ValueTask.CompletedTask;
    }


    public ValueTask SendToTopicAsync(string topicName, object message, DeliveryOptions? options = null)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var outgoing = Runtime.RoutingFor(message.GetType()).RouteToTopic(message, topicName, options);
        return persistOrSendAsync(outgoing);
    }

    public ValueTask SendToEndpointAsync(string endpointName, object message, DeliveryOptions? options = null)
    {
        if (endpointName == null)
        {
            throw new ArgumentNullException(nameof(endpointName));
        }

        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var outgoing = Runtime.RoutingFor(message.GetType())
            .RouteToEndpointByName(message, endpointName, options);

        return persistOrSendAsync(outgoing);
    }

    /// <summary>
    ///     Send to a specific destination rather than running the routing rules
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="destination">The destination to send to</param>
    /// <param name="message"></param>
    public ValueTask SendAsync<T>(Uri destination, T message, DeliveryOptions? options = null)
    {
        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var envelope = Runtime.RoutingFor(message.GetType())
            .RouteToDestination(message, destination, options);

        trackEnvelopeCorrelation(envelope);

        return persistOrSendAsync(envelope);
    }

    /// <summary>
    ///     Send a message that should be executed at the given time
    /// </summary>
    /// <param name="message"></param>
    /// <param name="time"></param>
    /// <param name="options"></param>
    /// <typeparam name="T"></typeparam>
    public ValueTask SchedulePublishAsync<T>(T message, DateTimeOffset time, DeliveryOptions? options = null)
    {
        options ??= new DeliveryOptions();
        options.ScheduledTime = time;

        return PublishAsync(message, options);
    }

    /// <summary>
    ///     Send a message that should be executed after the given delay
    /// </summary>
    /// <param name="message"></param>
    /// <param name="delay"></param>
    /// <param name="options"></param>
    /// <typeparam name="T"></typeparam>
    public ValueTask SchedulePublishAsync<T>(T message, TimeSpan delay, DeliveryOptions? options = null)
    {
        options ??= new DeliveryOptions();
        options.ScheduleDelay = delay;
        return PublishAsync(message, options);
    }

    private void trackEnvelopeCorrelation(Envelope[] outgoing)
    {
        foreach (var outbound in outgoing) trackEnvelopeCorrelation(outbound);
    }

    protected virtual void trackEnvelopeCorrelation(Envelope outbound)
    {
        outbound.Source = Runtime.Advanced.ServiceName;
        outbound.CorrelationId = CorrelationId;
    }

    protected async ValueTask persistOrSendAsync(params Envelope[] outgoing)
    {
        if (Transaction != null)
        {
            await Transaction.PersistAsync(outgoing.Where(isDurable).ToArray());

            _outstanding.Fill(outgoing);
        }
        else
        {
            foreach (var outgoingEnvelope in outgoing) await outgoingEnvelope.StoreAndForwardAsync();
        }
    }

    private bool isDurable(Envelope envelope)
    {
        // TODO -- should this be memoized? The test on envelope Destination anyway
        return envelope.Sender?.IsDurable ?? Runtime.Endpoints.GetOrBuildSendingAgent(envelope.Destination!).IsDurable;
    }
}
