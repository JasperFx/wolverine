using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;

namespace Wolverine.AzureServiceBus;

public class AzureServiceBusSubscriberConfiguration : SubscriberConfiguration<AzureServiceBusSubscriberConfiguration,
    AzureServiceBusEndpoint>
{
    protected AzureServiceBusSubscriberConfiguration(AzureServiceBusEndpoint queue) : base(queue)
    {
    }
}