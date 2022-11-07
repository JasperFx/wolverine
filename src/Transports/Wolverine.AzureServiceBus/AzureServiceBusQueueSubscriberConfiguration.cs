using Azure.Messaging.ServiceBus.Administration;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;

namespace Wolverine.AzureServiceBus;

public class AzureServiceBusQueueSubscriberConfiguration : SubscriberConfiguration<AzureServiceBusQueueSubscriberConfiguration,
    AzureServiceBusQueue>
{
    public AzureServiceBusQueueSubscriberConfiguration(AzureServiceBusQueue queue) : base(queue)
    {
    }
    
    /// <summary>
    /// Configure the underlying Azure Service Bus queue. This is only applicable when
    /// Wolverine is creating the queues
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusQueueSubscriberConfiguration ConfigureQueue(Action<CreateQueueOptions> configure)
    {
        add(e => configure(e.Options));
        return this;
    }
}