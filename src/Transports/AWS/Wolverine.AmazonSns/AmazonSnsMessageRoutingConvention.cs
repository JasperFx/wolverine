using Wolverine.AmazonSns.Internal;
using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.AmazonSns;

public class AmazonSnsMessageRoutingConvention : MessageRoutingConvention<AmazonSnsTransport,
    AmazonSnsListenerConfiguration, AmazonSnsSubscriberConfiguration, AmazonSnsMessageRoutingConvention>
{
    protected override (AmazonSnsListenerConfiguration, Endpoint) FindOrCreateListenerForIdentifier(string identifier,
        AmazonSnsTransport transport, Type messageType)
    {
        var topic = transport.EndpointForTopic(identifier);
        return (new AmazonSnsListenerConfiguration(topic), topic);
    }

    protected override (AmazonSnsSubscriberConfiguration, Endpoint) FindOrCreateSubscriber(string identifier,
        AmazonSnsTransport transport)
    {
        var topic = transport.EndpointForTopic(identifier);
        return (new AmazonSnsSubscriberConfiguration(topic), topic);
    }

    /// <summary>
    ///     Alternative syntax to specify the name for the topic that each message type will be sent
    /// </summary>
    /// <param name="namingRule"></param>
    /// <returns></returns>
    public AmazonSnsMessageRoutingConvention TopicNameForSender(Func<Type, string> namingRule)
    {
        return IdentifierForSender(namingRule);
    }
}
