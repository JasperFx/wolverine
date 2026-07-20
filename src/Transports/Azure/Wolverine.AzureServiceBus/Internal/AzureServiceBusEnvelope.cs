using Azure.Messaging.ServiceBus;
using JasperFx.Core;
using Wolverine.Transports;

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

    public AzureServiceBusEnvelope(ProcessSessionMessageEventArgs sessionArgs)
    {
        SessionArgs = sessionArgs;
        AzureMessage = sessionArgs.Message;
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
            else if (SessionArgs != null)
            {
                await SessionArgs.CompleteMessageAsync(AzureMessage, token);
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

        if (SessionArgs != null)
            return SessionArgs.DeferMessageAsync(AzureMessage, cancellationToken: token);

        if (ServiceBusReceiver != null)
            return ServiceBusReceiver.DeferMessageAsync(AzureMessage, cancellationToken: token);

        if (SessionReceiver != null)
            return SessionReceiver.DeferMessageAsync(AzureMessage, cancellationToken: token);

        return Task.CompletedTask;
    }

    public Task DeadLetterAsync(CancellationToken token, string? deadLetterReason = null, string? deadLetterErrorDescription = null)
    {
        // Copy the standard failure metadata headers stamped on this envelope onto the
        // dead lettered message's application properties so the diagnostics survive the
        // native move to the $DeadLetterQueue. GH-3474
        var propertiesToModify = buildDiagnosticProperties();

        if (Args != null)
            return Args.DeadLetterMessageAsync(AzureMessage, propertiesToModify, deadLetterReason, deadLetterErrorDescription, token);

        if (SessionArgs != null)
            return SessionArgs.DeadLetterMessageAsync(AzureMessage, propertiesToModify, deadLetterReason, deadLetterErrorDescription, token);

        if (ServiceBusReceiver != null)
            return ServiceBusReceiver.DeadLetterMessageAsync(AzureMessage, propertiesToModify, deadLetterReason, deadLetterErrorDescription, token);

        if (SessionReceiver != null)
            return SessionReceiver.DeadLetterMessageAsync(AzureMessage, propertiesToModify, deadLetterReason, deadLetterErrorDescription, token);

        return Task.CompletedTask;
    }

    private Dictionary<string, object>? buildDiagnosticProperties()
    {
        Dictionary<string, object>? properties = null;
        foreach (var key in DeadLetterQueueConstants.DiagnosticHeaders)
        {
            if (Headers.TryGetValue(key, out var value) && value != null)
            {
                properties ??= new Dictionary<string, object>();
                properties[key] = value;
            }
        }

        return properties;
    }

    private ProcessMessageEventArgs? Args { get; set; }
    private ProcessSessionMessageEventArgs? SessionArgs { get; set; }

    private ServiceBusReceivedMessage AzureMessage { get; }
    private ServiceBusSessionReceiver? SessionReceiver { get; }
    private ServiceBusReceiver? ServiceBusReceiver { get; }

    public Exception? Exception { get; set; }
    public bool IsCompleted { get; set; }
    public ServiceBusReceiver? Receiver { get; set; }
}