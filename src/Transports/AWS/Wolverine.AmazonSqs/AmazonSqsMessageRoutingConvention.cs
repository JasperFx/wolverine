using JasperFx.Core.Reflection;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AmazonSqs;

public class AmazonSqsMessageRoutingConvention : MessageRoutingConvention<AmazonSqsTransport,
    AmazonSqsListenerConfiguration, AmazonSqsSubscriberConfiguration, AmazonSqsMessageRoutingConvention>
{
    protected override (AmazonSqsListenerConfiguration, Endpoint) FindOrCreateListenerForIdentifier(string identifier,
        AmazonSqsTransport transport, Type messageType)
    {
        var queue = transport.EndpointForQueue(identifier);
        return (new AmazonSqsListenerConfiguration(queue), queue);
    }

    protected override (AmazonSqsSubscriberConfiguration, Endpoint) FindOrCreateSubscriber(string identifier,
        AmazonSqsTransport transport)
    {
        var queue = transport.EndpointForQueue(identifier);
        return (new AmazonSqsSubscriberConfiguration(queue), queue);
    }

    /// <summary>
    ///     Alternative syntax to specify the name for the queue that each message type will be sent
    /// </summary>
    /// <param name="namingRule"></param>
    /// <returns></returns>
    public AmazonSqsMessageRoutingConvention QueueNameForSender(Func<Type, string> namingRule)
    {
        return IdentifierForSender(namingRule);
    }

    /// <summary>
    /// GH-4524. Under <see cref="MultipleHandlerBehavior.Separated"/> every handler of a message type past the
    /// first listens on its own queue, named from the handler type the way RabbitMQ names its per-handler queue
    /// -- but SQS has no exchange to fan out from, so the sender publishes to each of those queues itself (see
    /// <see cref="FindOrCreateAdditionalSubscribers"/>). The first handler keeps the message type's own queue.
    /// </summary>
    protected override (AmazonSqsListenerConfiguration, Endpoint) FindOrCreateListenerForIdentifierUsingSeparatedHandler(string identifier,
        AmazonSqsTransport transport, Type messageType, Type handlerType)
    {
        var queue = transport.EndpointForQueue(QueueNameForSeparatedHandler(transport, handlerType));
        return (new AmazonSqsListenerConfiguration(queue), queue);
    }

    /// <summary>
    /// GH-4524. The per-handler queues of the separated handlers for <paramref name="messageType"/> in THIS
    /// application, which the sender publishes to alongside the message type's own queue. A publisher in
    /// another process knows nothing of this process's handlers, so it still publishes to the message type's
    /// queue only -- fan-out across processes needs SNS in front of the queues.
    /// </summary>
    protected override IEnumerable<(AmazonSqsSubscriberConfiguration, Endpoint)> FindOrCreateAdditionalSubscribers(
        string identifier, AmazonSqsTransport transport, Type messageType, IWolverineRuntime runtime)
    {
        foreach (var handlerType in SeparatedHandlerTypesFor(messageType, runtime))
        {
            var queue = transport.EndpointForQueue(QueueNameForSeparatedHandler(transport, handlerType));
            yield return (new AmazonSqsSubscriberConfiguration(queue), queue);
        }
    }

    /// <summary>
    /// GH-4524. When every local handler went to its own queue, nothing listens on the message type's queue
    /// here, so the sender leaves it alone rather than piling up copies nobody consumes
    /// </summary>
    protected override bool ShouldPublishToConventionalDestination(string identifier, AmazonSqsTransport transport,
        Type messageType, IWolverineRuntime runtime)
    {
        return !EveryHandlerIsSeparated(messageType, runtime);
    }

    internal static string QueueNameForSeparatedHandler(AmazonSqsTransport transport, Type handlerType)
    {
        return transport.MaybeCorrectName(handlerType.FullNameInCode());
    }
}