using Azure.Messaging.ServiceBus;

namespace Wolverine.AzureServiceBus.Internal;

public class AzureServiceBusEnvelope : Envelope
{
    public AzureServiceBusEnvelope(ServiceBusReceivedMessage message)
    {
        AzureMessage = message;
    }

    public ServiceBusReceivedMessage AzureMessage { get; }
}