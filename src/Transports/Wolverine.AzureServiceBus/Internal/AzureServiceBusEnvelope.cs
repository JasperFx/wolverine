using Azure.Messaging.ServiceBus;

namespace Wolverine.AzureServiceBus.Internal;

public class AzureServiceBusEnvelope : Envelope
{
    public ServiceBusReceivedMessage AzureMessage { get; }

    public AzureServiceBusEnvelope(ServiceBusReceivedMessage message)
    {
        AzureMessage = message;
    }
}