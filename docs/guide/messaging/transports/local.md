# Using Local Queueing

Using the `Wolverine.IMessageBus` service that is automatically registered in your system through 
the `IHostBuilder.UseWolverine()` extensions, you can either invoke message handlers inline, enqueue 
messages to local, in process queues, or schedule message execution within the system. All known message
handlers within a Wolverine application can be used from `IMessageBus` without any additional
explicit configuration.


## Publishing Messages Locally

The queueing is all based around the [TPL Dataflow library](https://docs.microsoft.com/en-us/dotnet/standard/parallel-programming/how-to-perform-action-when-a-dataflow-block-receives-data) objects from the [TPL Dataflow](https://docs.microsoft.com/en-us/dotnet/standard/parallel-programming/dataflow-task-parallel-library) library.
As such, you have a fair amount of control over parallelization and even some back pressure. These local queues can be used directly, or as a transport to accept messages sent through
`IMessageBus.SendAsync()` or `IMessageBus.PublishAsync()`. using the application's [message routing rules](/guide/messaging/subscriptions.html#routing-rules).


This feature is useful for asynchronous processing in web applications or really any kind of application where you need some parallelization or concurrency.

Some things to know about the local queues:

* Local worker queues can be durable, meaning that the enqueued messages are persisted first so that they aren't lost if the application is shut down before they're processed. More on that below.
* You can use any number of named local queues, and they don't even have to be declared upfront (might want to be careful with that though)
* Local worker queues utilize Wolverine's [error handling](/guide/handlers/error-handling) policies to selectively handle any detected exceptions from the [message handlers](/guide/handlers/).
* You can control the priority and parallelization of each individual local queue
* Message types can be routed to particular queues, **but by default Wolverine will route messages to an individual local queue for each message type that is named for the message type name**
* [Cascading messages](/guide/handlers/cascading) can be used with the local queues
* The local queues can be used like any other message transport and be the target of routing rules


## Explicitly Publish to a Specific Local Queue

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
    return bus.EndpointFor("highpriority").SendAsync(@event);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PublishingSamples.cs#L139-L156' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_iservicebus.enqueue_to_specific_worker_queue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Scheduling Local Execution

:::tip
If you need the command scheduling to be persistent or be persisted across service restarts, you'll need to enable the [message persistence](/guide/durability/) within Wolverine.
:::

The "scheduled execution" feature can be used with local execution within the same application. See [Scheduled Messages](/guide/messaging/message-bus.html#scheduling-message-delivery-or-execution) for more information. Use the `IMessageBus.ScheduleAsync()` extension methods like this:

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PublishingSamples.cs#L177-L194' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_schedule_job_locally' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->



## Explicit Local Message Routing

In the absence of any kind of routing rules, any message enqueued with `IMessageBus.PublishAsync()` will just be handled by a
local queue with the message type name. To override that choice on a message type by message type basis, you can use the `[LocalQueue]` attribute
on a message type:

<!-- snippet: sample_local_queue_routed_message -->
<a id='snippet-sample_local_queue_routed_message'></a>
```cs
[LocalQueue("important")]
public class ImportanceMessage;
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/LocalQueueMessage.cs#L7-L12' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_local_queue_routed_message' title='Start of snippet'>anchor</a></sup>
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

See [message routing rules](/guide/messaging/subscriptions.html#routing-rules) for more information.

## Conventional Local Messaging

Conventional local message routing is applied to every message type handled by the system that does not have some kind
of explicit message type routing rule. You can override the message type to local queue configuration with this syntax:

<!-- snippet: sample_local_queue_conventions -->
<a id='snippet-sample_local_queue_conventions'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Out of the box, this uses a separate local queue
        // for each message based on the message type name
        opts.Policies.ConfigureConventionalLocalRouting()

            // Or you can customize the usage of queues
            // per message type
            .Named(type => type.Namespace)

            // Optionally configure the local queues
            .CustomizeQueues((type, listener) => { listener.Sequential(); });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/EnqueueSamples.cs#L55-L72' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_local_queue_conventions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Disable Conventional Local Routing

Sometimes you'll want to disable the conventional routing to local queues, especially if you want to evenly distribute work across active
nodes in an application. To do so, use this syntax:

<!-- snippet: sample_disable_local_queue_routing -->
<a id='snippet-sample_disable_local_queue_routing'></a>
```cs
public static async Task disable_queue_routing()
{
    using var host = await Host.CreateDefaultBuilder()
        .UseWolverine(opts =>
        {
            // This will disable the conventional local queue
            // routing that would take precedence over other conventional
            // routing
            opts.Policies.DisableConventionalLocalRouting();

            // Other routing conventions. Rabbit MQ? SQS?
        }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/LocalQueueMessage.cs#L16-L31' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disable_local_queue_routing' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Configuring Local Queues

::: warning
The current default is for local queues to allow for parallel processing with the maximum number of parallel threads
set at the number of processors for the current machine. Likewise, the queues are unordered by default.
:::

You can configure durability or parallelization rules on single queues or conventional
configuration for queues with this usage:

<!-- snippet: sample_configuring_local_queues -->
<a id='snippet-sample_configuring_local_queues'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Explicit configuration for the local queue
        // by the message type it handles:
        opts.LocalQueueFor<Message1>()
            .UseDurableInbox()
            .Sequential();

        // Explicit configuration by queue name
        opts.LocalQueue("one")
            .Sequential();

        opts.LocalQueue("two")
            .MaximumParallelMessages(10)
            .UseDurableInbox();

        // Apply configuration options to all local queues,
        // but explicit changes to specific local queues take precedence
        opts.Policies.AllLocalQueues(x => x.UseDurableInbox());
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/EnqueueSamples.cs#L26-L50' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_local_queues' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Using IConfigureLocalQueue to Configure Local Queues <Badge type="tip" text="3.7" />

::: info
This feature was added in reaction to the newer "sticky" handler to local queue usage, but it's perfectly usable for
message types that are happily handled without any "sticky" handler configuration.
:::

The advent of ["sticky handlers"](/guide/handlers/sticky) or the [separated handler mode](/guide/handlers/#multiple-handlers-for-the-same-message-type) for better Wolverine usage in modular monoliths admittedly
made it a little harder to fine tune the local queue behavior for different message types or message handlers without understanding
the Wolverine naming conventions. To get back to leaning more on the type system, Wolverine introduced the static `IConfigureLocalQueue`
interface that can be implemented on any handler type to configure the local queue where that handler would run:

<!-- snippet: sample_IConfigureLocalQueue -->
<a id='snippet-sample_iconfigurelocalqueue'></a>
```cs
/// <summary>
/// Helps mark a handler to configure the local queue that its messages
/// would be routed to. It's probably only useful to use this with "sticky" handlers
/// that run on an isolated local queue
/// </summary>
public interface IConfigureLocalQueue
{
    static abstract void Configure(LocalQueueConfiguration configuration);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/Configuration/IConfigureLocalQueue.cs#L5-L17' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_iconfigurelocalqueue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
Static interfaces can only be used on non-static types, so even if all your message handler *methods* are static, the 
handler type itself cannot be static. Just a .NET quirk.
:::

To use this, just implement that interface on any message handler type:

<!-- snippet: sample_using_IConfigureLocalQueue -->
<a id='snippet-sample_using_iconfigurelocalqueue'></a>
```cs
public class MultipleMessage1Handler : IConfigureLocalQueue
{
    public static void Handle(MultipleMessage message)
    {
        
    }

    // This method is configuring the local queue that executes this
    // handler to be strictly ordered
    public static void Configure(LocalQueueConfiguration configuration)
    {
        configuration.Sequential();
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/configuring_local_queues.cs#L102-L119' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_iconfigurelocalqueue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MessagingConfigurationExamples.cs#L123-L136' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_localdurabletransportapp' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


See [Durable Inbox and Outbox Messaging](/guide/durability/) for more information.


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

        // And finally, this enrolls a queue into the persistent inbox
        // so that messages can happily be retained and processed
        // after the service is restarted
        opts.LocalQueue("four").UseDurableInbox();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PublishingSamples.cs#L16-L42' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_localqueuesapp' title='Start of snippet'>anchor</a></sup>
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

Calling `IMessageBus.SendAsync(new Message2())` would publish the message to the local "important" queue.
