using Azure.Messaging.ServiceBus;
using JasperFx.Core;

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

    public async Task CompleteAsync(CancellationToken token)
    {
        try
        {
            if (Args != null)
            {
                await Args.CompleteMessageAsync(AzureMessage, token);
            }
            else if (ServiceBusReceiver != null)
            {
                await ServiceBusReceiver.CompleteMessageAsync(AzureMessage, token);
            }
            else if (SessionReceiver != null)
            {
                await SessionReceiver.CompleteMessageAsync(AzureMessage, token);
            }
        }
        catch (ServiceBusException e)
        {
            if (e.Message.ContainsIgnoreCase("The lock supplied is invalid"))
            {
                return;
            }

            throw;
        }
    }

    public Task DeferAsync(CancellationToken token)
    {
        if (Args != null)
            return Args.DeferMessageAsync(AzureMessage, cancellationToken: token);

        if (ServiceBusReceiver != null)
            return ServiceBusReceiver.DeferMessageAsync(AzureMessage, cancellationToken: token);

        if (SessionReceiver != null)
            return SessionReceiver.DeferMessageAsync(AzureMessage, cancellationToken: token);

        return Task.CompletedTask;
    }

    public Task DeadLetterAsync(CancellationToken token, string? deadLetterReason = null, string? deadLetterErrorDescription = null)
    {
        if (Args != null)
            return Args.DeadLetterMessageAsync(AzureMessage, cancellationToken: token, deadLetterReason: deadLetterReason, deadLetterErrorDescription: deadLetterErrorDescription);

        if (ServiceBusReceiver != null)
            return ServiceBusReceiver.DeadLetterMessageAsync(AzureMessage, cancellationToken: token, deadLetterReason: deadLetterReason, deadLetterErrorDescription: deadLetterErrorDescription);

        if (SessionReceiver != null)
            return SessionReceiver.DeadLetterMessageAsync(AzureMessage, cancellationToken: token, deadLetterReason: deadLetterReason, deadLetterErrorDescription: deadLetterErrorDescription);

        return Task.CompletedTask;
    }

    private ProcessMessageEventArgs? Args { get; set; }

    private ServiceBusReceivedMessage AzureMessage { get; }
    private ServiceBusSessionReceiver? SessionReceiver { get; }
    private ServiceBusReceiver? ServiceBusReceiver { get; }

    public Exception? Exception { get; set; }
    public bool IsCompleted { get; set; }
    public ServiceBusReceiver? Receiver { get; set; }
}