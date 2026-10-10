using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AzureServiceBus;

public class AzureServiceBusMessageRoutingConvention
    : MessageRoutingConvention<AzureServiceBusTransport, AzureServiceBusQueueListenerConfiguration,
        AzureServiceBusQueueSubscriberConfiguration, AzureServiceBusMessageRoutingConvention>
{
    protected override (AzureServiceBusQueueListenerConfiguration, Endpoint) FindOrCreateListenerForIdentifier(
        string identifier,
        AzureServiceBusTransport transport, Type messageType)
    {
        var queue = transport.Queues[identifier];
        return (new AzureServiceBusQueueListenerConfiguration(queue), queue);
    }

    protected override (AzureServiceBusQueueSubscriberConfiguration, Endpoint) FindOrCreateSubscriber(string identifier,
        AzureServiceBusTransport transport)
    {
        var queue = transport.Queues[identifier];
        return (new AzureServiceBusQueueSubscriberConfiguration(queue), queue);
    }

    /// <summary>
    ///     Specify naming rules for the subscribing queue for message types
    /// </summary>
    /// <param name="namingRule"></param>
    /// <returns></returns>
    public AzureServiceBusMessageRoutingConvention QueueNameForSender(Func<Type, string?> namingRule)
    {
        return IdentifierForSender(namingRule);
    }

    /// <summary>
    /// GH-4525. Under <see cref="MultipleHandlerBehavior.Separated"/> every handler of a message type past the
    /// first listens on its own queue, named from the handler type the way RabbitMQ names its per-handler queue
    /// and the topic convention names its per-handler subscription. A queue does not fan out, so the sender
    /// publishes to each of those queues itself (see <see cref="FindOrCreateAdditionalSubscribers"/>). The first
    /// handler keeps the message type's own queue.
    /// </summary>
    protected override (AzureServiceBusQueueListenerConfiguration, Endpoint) FindOrCreateListenerForIdentifierUsingSeparatedHandler(
        string identifier, AzureServiceBusTransport transport, Type messageType, Type handlerType)
    {
        var queue = transport.Queues[QueueNameForSeparatedHandler(transport, handlerType)];
        return (new AzureServiceBusQueueListenerConfiguration(queue), queue);
    }

    /// <summary>
    /// GH-4525. The per-handler queues of the separated handlers for <paramref name="messageType"/> in THIS
    /// application, which the sender publishes to alongside the message type's own queue. A publisher in
    /// another process knows nothing of this process's handlers, so it still publishes to the message type's
    /// queue only -- fan-out across processes is what the topic and subscription convention is for.
    /// </summary>
    protected override IEnumerable<(AzureServiceBusQueueSubscriberConfiguration, Endpoint)> FindOrCreateAdditionalSubscribers(
        string identifier, AzureServiceBusTransport transport, Type messageType, IWolverineRuntime runtime)
    {
        foreach (var handlerType in SeparatedHandlerTypesFor(messageType, runtime))
        {
            var queue = transport.Queues[QueueNameForSeparatedHandler(transport, handlerType)];
            yield return (new AzureServiceBusQueueSubscriberConfiguration(queue), queue);
        }
    }

    /// <summary>
    /// GH-4525. When every local handler went to its own queue, nothing listens on the message type's queue
    /// here, so the sender leaves it alone rather than piling up copies nobody consumes
    /// </summary>
    protected override bool ShouldPublishToConventionalDestination(string identifier, AzureServiceBusTransport transport,
        Type messageType, IWolverineRuntime runtime)
    {
        return !EveryHandlerIsSeparated(messageType, runtime);
    }

    internal static string QueueNameForSeparatedHandler(AzureServiceBusTransport transport, Type handlerType)
    {
        return transport.MaybeCorrectName(handlerType.FullNameInCode());
    }
}