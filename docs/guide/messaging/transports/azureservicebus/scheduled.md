# Scheduled Delivery

::: info
This functionality as introduced in Wolverine 1.6.0
:::

WolverineFx.AzureServiceBus now supports [native Azure Service Bus scheduled delivery](https://learn.microsoft.com/en-us/azure/service-bus-messaging/message-sequencing).
There's absolutely nothing you need to do explicitly to enable this functionality.

So for message types that are routed to Azure Service Bus queues or topics, you can use this functionality:

<!-- snippet: sample_send_delayed_message -->
<a id='snippet-sample_send_delayed_message'></a>
```cs
private async Task SendScheduledMessage(IMessageContext bus, Guid invoiceId)
{
    var message = new ValidateInvoiceIsNotLate
    {
        InvoiceId = invoiceId
    };

    // Schedule the message to be processed in a certain amount
    // of time
    await bus.ScheduleAsync(message, 30.Days());

    // Schedule the message to be processed at a certain time
    await bus.ScheduleAsync(message, DateTimeOffset.Now.AddDays(30));
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PublishingSamples.cs#L153-L169' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_send_delayed_message' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Canceling a scheduled message

For native Azure Service Bus schedules, use `ScheduleWithCancellationAsync()` to get a handle:

```cs
var scheduled = await bus.ScheduleWithCancellationAsync(message, TimeSpan.FromMinutes(30));
var outcomes = await scheduled.CancelAsync(cancellationToken);
```

The handle is kept in memory and only works while the Wolverine runtime that created it is running. It does not survive
an application restart.
`CancelAsync()` reports one outcome per routed destination. `NotScheduledYet` means the sender has not received an Azure
sequence number yet; no cancellation request was queued, and the call can be retried. `CancellationAccepted` means Azure
accepted the request, though activation can race with cancellation. `NoLongerScheduled` means Azure no longer recognizes
that sequence as scheduled; the message may have activated or been removed. Other transports report `NotSupported`.
If the requested time passes before the sender reaches Azure, the result is `NotScheduled` and the message is sent
immediately.

This API keeps the usual Wolverine buffering and outbox behavior. The handle can only cancel the broker schedule after
the outgoing envelope reaches the Azure sender.

And also use Azure Service Bus scheduled delivery for scheduled retries (assuming that the listening endpoint was an **inline** Azure Service Bus listener):

<!-- snippet: sample_using_scheduled_retry -->
<a id='snippet-sample_using_scheduled_retry'></a>
```cs
using var host = Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Policies.OnException<TimeoutException>()
            // Just retry the message again on the
            // first failure
            .RetryOnce()

            // On the 2nd failure, put the message back into the
            // incoming queue to be retried later
            .Then.Requeue()

            // On the 3rd failure, retry the message again after a configurable
            // cool-off period. This schedules the message
            .Then.ScheduleRetry(15.Seconds())

            // On the next failure, move the message to the dead letter queue
            .Then.MoveToErrorQueue();

    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/ExceptionHandling.cs#L85-L107' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_scheduled_retry' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
