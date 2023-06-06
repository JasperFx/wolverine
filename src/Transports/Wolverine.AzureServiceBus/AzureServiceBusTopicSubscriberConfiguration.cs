using Azure.Messaging.ServiceBus.Administration;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;

namespace Wolverine.AzureServiceBus;

public class AzureServiceBusTopicSubscriberConfiguration : SubscriberConfiguration<
    AzureServiceBusTopicSubscriberConfiguration,
    AzureServiceBusTopic>
{
    public AzureServiceBusTopicSubscriberConfiguration(AzureServiceBusTopic endpoint) : base(endpoint)
    {
    }

    /// <summary>
    ///     Configure the underlying Azure Service Bus Topic. This is only applicable when
    ///     Wolverine is creating the Topics
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusTopicSubscriberConfiguration ConfigureTopic(Action<CreateTopicOptions> configure)
    {
        add(e => configure(e.Options));
        return this;
    }
}