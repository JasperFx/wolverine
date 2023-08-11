using Azure.Messaging.ServiceBus;

namespace Wolverine.AzureServiceBus.Internal;

public class AzureServiceBusEnvelope : Envelope
{
    public AzureServiceBusEnvelope(ServiceBusReceivedMessage message)
    {
        AzureMessage = message;
    }

    public AzureServiceBusEnvelope(ProcessMessageEventArgs args)
    {
        Args = args;
        AzureMessage = args.Message;
    }

    public ProcessMessageEventArgs Args { get; set; }

    public ServiceBusReceivedMessage AzureMessage { get; }
    public Exception Exception { get; set; }
    public bool IsCompleted { get; set; }
    public ServiceBusReceiver? Receiver { get; set; }
}