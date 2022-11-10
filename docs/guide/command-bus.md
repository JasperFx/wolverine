# Wolverine as Command Bus

::: tip
Both `IMessagePublisher` and `IMessageContext` also implement `ICommandBus` to enable users
to use a mix of local and remote message handling at one time.
:::

Using the `Wolverine.ICommandBus` service that is automatically registered in your system through 
the `IHostBuilder.UseWolverine()` extensions, you can either invoke message handlers inline, enqueue 
messages to local, in process queues, or schedule message execution within the system. All known message
handlers within a Wolverine application can be used from `ICommandBus` without any additional
configuration as some other tools require.

## Invoking Message Handling

To execute the message processing immediately, use this syntax:

snippet: sample_invoke_locally

Note that this feature does utilize any registered [retry or retry with cooldown error handling rules](/guide/handlers/error-handling)
for potentially transient errors.

## Enqueueing Messages Locally

You can queue up messages to be executed locally and asynchronously in a background thread:

<!-- snippet: sample_enqueue_locally -->
<a id='snippet-sample_enqueue_locally'></a>
```cs
public static async Task enqueue_locally(ICommandBus bus)
{
    // Enqueue a message to the local worker queues
    await bus.EnqueueAsync(new Message1());

}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/EnqueueSamples.cs#L8-L16' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_enqueue_locally' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The queueing is all based around the [TPL Dataflow library](https://docs.microsoft.com/en-us/dotnet/standard/parallel-programming/how-to-perform-action-when-a-dataflow-block-receives-data) objects from the [TPL Dataflow](https://docs.microsoft.com/en-us/dotnet/standard/parallel-programming/dataflow-task-parallel-library) library.
As such, you have a fair amount of control over parallelization and even some back pressure. These local queues can be used directly, or as a transport to accept messages sent through
`IMessagePublisher.SendAsync()` or `IMessagePublisher.PublishAsync()`. using the application's [message routing rules](/guide/messaging/#routing-rules).

This feature is useful for asynchronous processing in web applications or really any kind of application where you need some parallelization or concurrency.

Some things to know about the local queues:

* Local worker queues can be durable, meaning that the enqueued messages are persisted first so that they aren't lost if the application is shut down before they're processed. More on that below.
* You can use any number of named local queues, and they don't even have to be declared upfront (might want to be careful with that though)
* Local worker queues utilize Wolverine's [error handling](/guide/messages/error-handling) policies to selectively handle any detected exceptions from the [message handlers](/guide/messages/handlers).
* You can control the priority and parallelization of each individual local queue
* Message types can be routed to particular queues
* [Cascading messages](/guide/messages/handlers.html#cascading-messages-from-actions) can be used with the local queues
* The local queues can be used like any other message transport and be the target of routing rules


## Explicitly Enqueue to a Specific Local Queue

If you want to enqueue a message locally to a specific worker queue, you can use this syntax:

<!-- snippet: sample_IServiceBus.Enqueue_to_specific_worker_queue -->
<a id='snippet-sample_iservicebus.enqueue_to_specific_worker_queue'></a>
```cs
public ValueTask EnqueueToQueue(IMessageContext bus)
{
    var @event = new InvoiceCreated
    {
        Time = DateTimeOffset.Now,
        Purchaser = "Guy Fieri",
        Amount = 112.34,
        Item = "Cookbook"
    };

    // Put this message in a local worker
    // queue named 'highpriority'
    return bus.EnqueueAsync(@event, "highpriority");
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PublishingSamples.cs#L106-L121' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_iservicebus.enqueue_to_specific_worker_queue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Scheduling Local Execution

:::tip
If you need the command scheduling to be persistent or be persisted across service restarts, you'll need to enable the [message persistence](/guide/persistence/) within Wolverine.
:::

The "scheduled execution" feature can be used with local execution within the same application. See [Scheduled Messages](/guide/scheduled) for more information. Use the `ICommandBus.ScheduleAsync()` methods like this:

<!-- snippet: sample_schedule_job_locally -->
<a id='snippet-sample_schedule_job_locally'></a>
```cs
public async Task ScheduleLocally(IMessageContext bus, Guid invoiceId)
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PublishingSamples.cs#L140-L155' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_schedule_job_locally' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## The Default Queue

Out of the box, each Wolverine application has a default queue named "default". In the absence of any
other routing rules, all messages enqueued to `ICommandBus` will be published to this queue. The default in memory
queue can be configured like this:

<!-- snippet: sample_ConfigureDefaultQueue -->
<a id='snippet-sample_configuredefaultqueue'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.DefaultLocalQueue.MaximumParallelMessages(3);
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/ConfigureDurableLocalQueueApp.cs#L28-L36' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuredefaultqueue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Explicit Local Message Routing

In the absence of any kind of routing rules, any message enqueued with `ICommandBus.Enqueue()` will just be handled by the
*default* local queue. To override that choice on a message type by message type basis, you can use the `[LocalQueue]` attribute
on a message type:

<!-- snippet: sample_local_queue_routed_message -->
<a id='snippet-sample_local_queue_routed_message'></a>
```cs
[LocalQueue("important")]
public class ImportanceMessage
{

}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/LocalQueueMessage.cs#L5-L12' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_local_queue_routed_message' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Otherwise, you can take advantage of Wolverine's message routing rules like this:

<!-- snippet: sample_LocalTransportApp -->
<a id='snippet-sample_localtransportapp'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Publish Message2 messages to the "important"
        // local queue
        opts.PublishMessage<Message2>()
            .ToLocalQueue("important");
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MessagingConfigurationExamples.cs#L104-L115' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_localtransportapp' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The routing rules and/or `[LocalQueue]` routing is also honored for cascading messages, meaning that any message that is handled inside a Wolverine system could publish cascading messages to the local worker queues.

See [message routing rules](/guide/messaging/#routing-rules) for more information.

## Conventional Local Messaging

You can apply a conventional routing for message types to local queues using this syntax:

snippet: sample_local_queue_conventions

## Configuring Local Queues

You can configure durability or parallelization rules on single queues or conventional
configuration for queues with this usage:

snippet: sample_configuring_local_queues

## Durable Local Messages

The local worker queues can optionally be designated as "durable," meaning that local messages would be persisted until they can be successfully processed to provide a guarantee that the message will be successfully processed in the case of the running application faulting or having been shut down prematurely (assuming that other nodes are running or it's restarted later of course).

Here is an example of configuring a local queue to be durable:

<!-- snippet: sample_LocalDurableTransportApp -->
<a id='snippet-sample_localdurabletransportapp'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Make the default local queue durable
        opts.DefaultLocalQueue.UseDurableInbox();

        // Or do just this by name
        opts.LocalQueue("important")
            .UseDurableInbox();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MessagingConfigurationExamples.cs#L124-L137' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_localdurabletransportapp' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


See [Persistent Messaging](http://localhost:5050/guide/persistence/) for more information.


## Configuring Parallelization and Execution Properties

The queues are built on top of the TPL Dataflow library, so it's pretty easy to configure parallelization (how many concurrent messages could be handled by a queue). Here's an example of how to establish this:

<!-- snippet: sample_LocalQueuesApp -->
<a id='snippet-sample_localqueuesapp'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Force a local queue to be
        // strictly first in, first out
        // with no more than a single
        // thread handling messages enqueued
        // here

        // Use this option if message ordering is
        // important
        opts.LocalQueue("one")
            .Sequential();

        // Specify the maximum number of parallel threads
        opts.LocalQueue("two")
            .MaximumParallelMessages(5);

        // Or just edit the ActionBlock options directly
        opts.LocalQueue("three")
            .ConfigureExecution(options =>
            {
                options.MaxDegreeOfParallelism = 5;
                options.BoundedCapacity = 1000;
            });

        // And finally, this enrolls a queue into the persistent inbox
        // so that messages can happily be retained and processed
        // after the service is restarted
        opts.LocalQueue("four").UseDurableInbox();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PublishingSamples.cs#L14-L49' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_localqueuesapp' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Local Queues as a Messaging Transport

::: tip warning
The local transport is used underneath the covers by Wolverine for retrying
locally enqueued messages or scheduled messages that may have initially failed.
:::

In the sample Wolverine configuration shown below:

<!-- snippet: sample_LocalTransportApp -->
<a id='snippet-sample_localtransportapp'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Publish Message2 messages to the "important"
        // local queue
        opts.PublishMessage<Message2>()
            .ToLocalQueue("important");
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MessagingConfigurationExamples.cs#L104-L115' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_localtransportapp' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Calling `IMessagePublisher.Send(new Message2())` would publish the message to the local "important" queue.
