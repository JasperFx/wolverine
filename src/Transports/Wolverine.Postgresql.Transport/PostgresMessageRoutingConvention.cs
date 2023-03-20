using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.Transports.Postgresql;

public sealed class PostgresMessageRoutingConvention
    : MessageRoutingConvention<PostgresTransport, PostgresQueueListenerConfiguration,
        PostgresQueueSubscriberConfiguration, PostgresMessageRoutingConvention>
{
    protected override (PostgresQueueListenerConfiguration, Endpoint) findOrCreateListenerForIdentifier(
        string identifier,
        PostgresTransport transport)
    {
        var queue = transport.Queues[identifier];
        return (new PostgresQueueListenerConfiguration(queue), queue);
    }

    protected override (PostgresQueueSubscriberConfiguration, Endpoint) findOrCreateSubscriber(string identifier,
        PostgresTransport transport)
    {
        var queue = transport.Queues[identifier];
        return (new PostgresQueueSubscriberConfiguration(queue), queue);
    }

    /// <summary>
    ///     Specify naming rules for the subscribing queue for message types
    /// </summary>
    /// <param name="namingRule"></param>
    /// <returns></returns>
    public PostgresMessageRoutingConvention QueueNameForSender(Func<Type, string?> namingRule)
    {
        return IdentifierForSender(namingRule);
    }
}