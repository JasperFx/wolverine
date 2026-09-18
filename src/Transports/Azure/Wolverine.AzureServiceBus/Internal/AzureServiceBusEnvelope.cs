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

            // GH-4068: mark the settle here rather than in each caller. Only the _defer blocks used
            // to set this, so the "already completed" guards never saw a completion that came from
            // a _complete block -- most importantly the one BufferedReceiver issues on receipt.
            IsCompleted = true;
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

    public async Task DeadLetterAsync(CancellationToken token, string? deadLetterReason = null, string? deadLetterErrorDescription = null)
    {
        // Copy the standard failure metadata headers stamped on this envelope onto the
        // dead lettered message's application properties so the diagnostics survive the
        // native move to the $DeadLetterQueue. GH-3474
        var propertiesToModify = buildDiagnosticProperties();

        if (Args != null)
        {
            await Args.DeadLetterMessageAsync(AzureMessage, propertiesToModify, deadLetterReason, deadLetterErrorDescription, token);
        }
        else if (SessionArgs != null)
        {
            await SessionArgs.DeadLetterMessageAsync(AzureMessage, propertiesToModify, deadLetterReason, deadLetterErrorDescription, token);
        }
        else if (ServiceBusReceiver != null)
        {
            await ServiceBusReceiver.DeadLetterMessageAsync(AzureMessage, propertiesToModify, deadLetterReason, deadLetterErrorDescription, token);
        }
        else if (SessionReceiver != null)
        {
            await SessionReceiver.DeadLetterMessageAsync(AzureMessage, propertiesToModify, deadLetterReason, deadLetterErrorDescription, token);
        }
        else
        {
            // No receiver of any kind, so nothing was settled and there is nothing to mark.
            return;
        }

        // GH-4481: DeadLetterMessageAsync is a settle disposition -- it consumes the delivery's lock
        // exactly as CompleteAsync does -- so mark it the way the sibling CompleteAsync does, GH-4068
        // having put IsCompleted there for the same reason. The settle that follows is issued by core and
        // cannot see this one otherwise.
        //
        // MoveToErrorQueue.ExecuteAsync -- and NoHandlerContinuation on the unknown-message-type path --
        // call lifecycle.CompleteAsync() unconditionally after MoveToDeadLetterQueueAsync, and must: for a
        // transport whose MoveToErrorsAsync only sends a COPY (SQS, GCP Pub/Sub) that trailing call is the
        // only thing that ever settles the original. HasBeenAcked is how a transport that already settled
        // opts out of it -- MessageContext.CompleteAsync short-circuits on it, which is what RabbitMQ's two
        // dead-letter paths rely on. Azure Service Bus was the one settling transport that never set it, so
        // its trailing complete always went to the broker on a lock this call had already consumed: an
        // invisible "the lock supplied is invalid" on a normal entity, and on a SESSION entity a
        // SessionLockLost that forces the AMQP link closed and reopened before the next session is accepted.
        //
        // Only on success: a move that threw settled nothing, and that delivery has to stay eligible for
        // the lock lapse and redelivery its settle block's terminal give-up (GH-4012 item 5) relies on.
        IsCompleted = true;
        HasBeenAcked = true;
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

    // GH-4049: internal rather than private so tests can tell a redelivered copy from the original by its
    // broker-assigned SequenceNumber -- the observable difference between a native reschedule and an in-process one.
    internal ServiceBusReceivedMessage AzureMessage { get; }
    private ServiceBusSessionReceiver? SessionReceiver { get; }
    private ServiceBusReceiver? ServiceBusReceiver { get; }

    public Exception? Exception { get; set; }
    public bool IsCompleted { get; set; }
    public ServiceBusReceiver? Receiver { get; set; }
}