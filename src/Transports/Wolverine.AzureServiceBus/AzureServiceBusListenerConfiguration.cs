using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;

namespace Wolverine.AzureServiceBus;

public class AzureServiceBusListenerConfiguration : ListenerConfiguration<AzureServiceBusListenerConfiguration, AzureServiceBusEndpoint>
{
    public AzureServiceBusListenerConfiguration(AzureServiceBusEndpoint queue) : base(queue)
    {
    }
}