using Wolverine.AmazonSqs.Internal;
using Wolverine.Configuration;
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

    protected override (AmazonSqsListenerConfiguration, Endpoint) FindOrCreateListenerForIdentifierUsingSeparatedHandler(string identifier,
        AmazonSqsTransport transport, Type messageType, Type handlerType)
    {
        throw new NotSupportedException(
            "The AWS SQS transport does not (yet) support conventional routing to multiple handlers in the same application. You will have to resort to explicit routing.");
    }
}