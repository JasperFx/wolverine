using Azure.Messaging.ServiceBus;

namespace Wolverine.AzureServiceBus.Internal;

public class AzureServiceBusEnvelope : Envelope
{
    public AzureServiceBusEnvelope(ServiceBusReceivedMessage message, ServiceBusSessionReceiver sessionReceiver)
    {
        AzureMessage = message;
        SessionReceiver = sessionReceiver;
    }

    public AzureServiceBusEnvelope(ProcessMessageEventArgs args)
    {
        Args = args;
        AzureMessage = args.Message;
    }

    public AzureServiceBusEnvelope(ServiceBusReceivedMessage message, ServiceBusReceiver sessionReceiver)
    {
        AzureMessage = message;
        ServiceBusReceiver = sessionReceiver;
    }

    public Task CompleteAsync(CancellationToken token)
    {
        return Args?.CompleteMessageAsync(AzureMessage, token) ?? ServiceBusReceiver?.CompleteMessageAsync(AzureMessage, token) ??
            SessionReceiver?.CompleteMessageAsync(AzureMessage, token) ?? Task.CompletedTask;
    }

    public Task DeferAsync(CancellationToken token)
    {
        return Args?.DeferMessageAsync(AzureMessage, cancellationToken: token) ?? ServiceBusReceiver?.DeferMessageAsync(AzureMessage, cancellationToken: token) ??
            SessionReceiver?.DeferMessageAsync(AzureMessage, cancellationToken: token) ?? Task.CompletedTask;
    }

    public Task DeadLetterAsync(CancellationToken token, string? deadLetterReason = null, string? deadLetterErrorDescription = null)
    {
        return Args?.DeadLetterMessageAsync(AzureMessage, cancellationToken: token, deadLetterReason: deadLetterReason, deadLetterErrorDescription:deadLetterErrorDescription)
               ?? ServiceBusReceiver?.DeadLetterMessageAsync(AzureMessage, cancellationToken: token, deadLetterReason: deadLetterReason, deadLetterErrorDescription:deadLetterErrorDescription)
               ?? SessionReceiver?.DeadLetterMessageAsync(AzureMessage, cancellationToken: token, deadLetterReason: deadLetterReason, deadLetterErrorDescription:deadLetterErrorDescription) ?? Task.CompletedTask;
    }

    private ProcessMessageEventArgs? Args { get; set; }

    private ServiceBusReceivedMessage AzureMessage { get; }
    private ServiceBusSessionReceiver? SessionReceiver { get; }
    private ServiceBusReceiver? ServiceBusReceiver { get; }

    public Exception? Exception { get; set; }
    public bool IsCompleted { get; set; }
    public ServiceBusReceiver? Receiver { get; set; }
}