# Queue, Topic, and Binding Management

::: tip
Wolverine assumes that exchanges should be "fanout" unless explicitly configured
otherwise
:::

Use `AutoProvision()` to let Wolverine check for and create missing Rabbit MQ
[exchanges, queues, or bindings](https://www.rabbitmq.com/tutorials/amqp-concepts.html)
on demand, when it needs them. This enables Wolverine to manage the Rabbit MQ
objects declared in the application configuration, but does not force every
resource in the application to be set up during application startup.

<!-- snippet: sample_publish_to_rabbitmq_routing_key -->
<a id='snippet-sample_publish_to_rabbitmq_routing_key'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRabbitMq(rabbit => { rabbit.HostName = "localhost"; })
            // I'm declaring an exchange, a queue, and the binding
            // key that we're referencing below.
            // This is NOT MANDATORY, but rather just allows Wolverine to
            // control the Rabbit MQ object lifecycle
            .DeclareExchange("exchange1", ex => { ex.BindQueue("queue1", "key1"); })

            // Lets Wolverine create any missing declared Rabbit MQ objects
            // on demand when it needs them
            .AutoProvision();

        opts.PublishAllMessages().ToRabbitExchange("exchange1");
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L317-L336' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publish_to_rabbitmq_routing_key' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

At development time -- or occasionally in production systems -- you may want to have the messaging
queues purged of any old messages at application startup time. Wolverine supports that with
Rabbit MQ using the `AutoPurgeOnStartup()` declaration:

<!-- snippet: sample_autopurge_rabbitmq -->
<a id='snippet-sample_autopurge_rabbitmq'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRabbitMq()
            .AutoPurgeOnStartup();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L341-L349' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_autopurge_rabbitmq' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or you can be more selective and only have certain queues of volatile messages purged
at startup as shown below:

<!-- snippet: sample_autopurge_selective_queues -->
<a id='snippet-sample_autopurge_selective_queues'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRabbitMq()
            .DeclareQueue("queue1")
            .DeclareQueue("queue2", q => q.PurgeOnStartup = true);
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L354-L363' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_autopurge_selective_queues' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To force setup at application startup, use the [JasperFx stateful resource](https://jasperfx.github.io/oakton/guide/host/resources.html)
model instead. `AddResourceSetupOnStartup()` registers an `IHostedService` that
sets up every known JasperFx resource when the application starts. That includes
Marten database objects, Wolverine durability database objects, and Wolverine
message-broker objects. This works independently of `AutoProvision()`.

Use `AutoProvision()` for on-demand Rabbit MQ provisioning, or use
`AddResourceSetupOnStartup()` to provision all known JasperFx resources eagerly
at startup. You can use both when you want eager startup setup plus on-demand
provisioning for resources discovered later.

<!-- snippet: sample_kitchen_sink_bootstrapping -->
<a id='snippet-sample_kitchen_sink_bootstrapping'></a>
```cs
var builder = WebApplication.CreateBuilder(args);

builder.Host.ApplyJasperFxExtensions();

builder.Host.UseWolverine(opts =>
{
    // I'm setting this up to publish to the same process
    // just to see things work
    opts.PublishAllMessages()
        .ToRabbitExchange("issue_events", exchange => exchange.BindQueue("issue_events"))
        .UseDurableOutbox();

    opts.ListenToRabbitQueue("issue_events").UseDurableInbox();

    opts.UseRabbitMq(factory =>
    {
        // Just connecting with defaults, but showing
        // how you *could* customize the connection to Rabbit MQ
        factory.HostName = "localhost";
        factory.Port = 5672;
    })
        
    // Use for on-demand Rabbit MQ provisioning in addition to bootstrap time below
    .AutoProvision();
});

// Registers an IHostedService that sets up every known JasperFx resource
// at startup, including Marten/Wolverine database objects and Rabbit MQ objects
builder.Services.AddResourceSetupOnStartup();

// Just pumping out a bunch of messages so we can see
// statistics
builder.Services.AddHostedService<Worker>();

builder.Services.AddMarten(opts =>
{
    // I think you would most likely pull the connection string from
    // configuration like this:
    // var martenConnectionString = builder.Configuration.GetConnectionString("marten");
    // opts.Connection(martenConnectionString);

    opts.Connection(Servers.PostgresConnectionString);
    opts.DatabaseSchemaName = "issues";

    // Just letting Marten know there's a document type
    // so we can see the tables and functions created on startup
    opts.RegisterDocumentType<Issue>();

    // I'm putting the inbox/outbox tables into a separate "issue_service" schema
}).IntegrateWithWolverine(x => x.MessageStorageSchemaName = "issue_service");

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

// Actually important to return the exit code here!
return await app.RunJasperFxCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/KitchenSink/MartenAndRabbitIssueService/Program.cs#L11-L74' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_kitchen_sink_bootstrapping' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that this stateful resource model is also available at the command line as well for deploy time
management.

## Externally-Owned Queues and Exchanges <Badge type="tip" text="6.6" />

Sometimes a queue or exchange your application uses is owned and managed by a *different* system,
and the identity your application connects with simply does not have the `configure` or `delete`
permissions to create or remove it. In that case you want Wolverine to *use* the queue or exchange,
but never try to declare it at startup (even with `AutoProvision()` turned on) and never delete it
during a resource teardown. Mark the endpoint as `ExternallyOwned()` to get exactly that behavior:

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRabbitMq()
            // AutoProvision is on for the resources this app *does* own...
            .AutoProvision();

        // ...but this queue belongs to another team. Listen to it,
        // but never declare it at startup or delete it on teardown,
        // and don't try to set up or tear down its bindings.
        opts.ListenToRabbitQueue("shared-orders")
            .ExternallyOwned();

        // Same idea for an exchange we publish to but do not own
        opts.PublishMessage<OrderPlaced>()
            .ToRabbitExchange("shared-events")
            .ExternallyOwned();
    }).StartAsync();
```

When an endpoint is marked `ExternallyOwned()`, Wolverine will:

* Skip declaring the queue or exchange at startup, regardless of `AutoProvision()`
* Skip deleting it during a `resources teardown` (or the Oakton/JasperFx resource commands)
* Skip setting up or tearing down any bindings for that resource

This applies to queue listeners, queue subscribers, and exchange subscribers alike.

::: tip
`ExternallyOwned()` is distinct from `DeclarePassive`. A `DeclarePassive` exchange still makes a
*passive* declaration against the broker at startup to verify that the resource already exists
(failing fast if it does not), whereas an externally-owned resource never touches the broker for
declaration at all. As of Wolverine 6.6, a `DeclarePassive` exchange is also left alone during
resource teardown — Wolverine will not delete a resource it only verified rather than created.
:::

## Exchange-to-Exchange Bindings

Wolverine supports [RabbitMQ exchange-to-exchange bindings](https://www.rabbitmq.com/docs/e2e), which allow you
to route messages between exchanges before they reach a queue. This is useful for building message routing
topologies where a source exchange fans out to multiple destination exchanges, each with their own queue bindings.

You can declare exchange-to-exchange bindings using the fluent `BindExchange().ToExchange()` syntax:

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRabbitMq()
            .AutoProvision()

            // Bind source exchange to destination exchange with a routing key
            .BindExchange("source-exchange").ToExchange("destination-exchange", "routing.key")

            // The destination exchange still needs a queue binding for consumers
            .BindExchange("destination-exchange").ToQueue("my-queue", "routing.key");

        opts.PublishAllMessages().ToRabbitExchange("source-exchange");
        opts.ListenToRabbitQueue("my-queue");
    }).StartAsync();
```

When `AutoProvision()` is enabled, Wolverine declares the exchanges and creates the
exchange-to-exchange bindings when they are needed. Both the source and destination
exchanges are created if they do not already exist. Alternatively, add
`AddResourceSetupOnStartup()` to force this setup during application startup.

## Runtime Declaration

From a user request, there are some extension methods in the WolverineFx.RabbitMQ Nuget off of `IWolverineRuntime` that will enable you to 
first declare new exchanges, queues, and bindings at runtime, and also enable you to "unbind" a queue from an exchange. That
syntax is shown below:

<!-- snippet: sample_dynamic_creation_of_rabbit_mq_objects -->
<a id='snippet-sample_dynamic_creation_of_rabbit_mq_objects'></a>
```cs
// _host is an IHost
var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();

// Declare new Exchanges, Queues, and Bindings at runtime
runtime.ModifyRabbitMqObjects(o =>
{
    var queue = o.DeclareQueue(queueName);
    var exchange = o.DeclareExchange(exchangeName);
    queue.BindExchange(exchange.ExchangeName, bindingKey);
});

// Unbind a queue from an exchange
runtime.UnBindRabbitMqQueue(queueName, exchangeName, bindingKey);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/dynamic_object_creation_smoke_tests.cs#L34-L49' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_dynamic_creation_of_rabbit_mq_objects' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Quorum Queues or Streams <Badge type="tip" text="3.10" />

Wolverine can utilize [Rabbit MQ Quorum Queues](https://www.rabbitmq.com/docs/quorum-queues) or [Rabbit MQ Streams](https://www.rabbitmq.com/docs/streams), but ["Classic" queues](https://www.rabbitmq.com/docs/classic-queues) are the default. The only 
real difference as far as Wolverine is concerned is how the queues are declared to Rabbit MQ itself. Wolverine's internals
are largely not impacted otherwise.

Here are your options for configuring one or many queues as opting into being a "Quorum Queue" or a "Stream":

<!-- snippet: sample_configuring_quorum_or_streams_in_rabbit_mq -->
<a id='snippet-sample_configuring_quorum_or_streams_in_rabbit_mq'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    opts
        .UseRabbitMq(builder.Configuration.GetConnectionString("rabbit")!)
        
        // You can configure the queue type for declaration with this
        // usage as well
        .DeclareQueue("stream", q => q.QueueType = QueueType.stream)

        // Use quorum queues by default as a policy
        .UseQuorumQueues()

        // Or instead use streams
        .UseStreamsAsQueues();

    opts.ListenToRabbitQueue("quorum1")
        // Override the queue type in declarations for a
        // single queue, and the explicit configuration will win
        // out over any policy or convention
        .QueueType(QueueType.quorum);
    
    
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L568-L594' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_quorum_or_streams_in_rabbit_mq' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

There are just a few things to know:

* Wolverine's internal reply or control queues will still be declared as "classic" so they can be non-durable
* Streams cannot be purged, and Wolverine ignores the `AutoPurgeOnStartup()` setting for streams

## Quorum Queues and the Durable Inbox

If you're using quorum queues behind a [durable inbox](/guide/durability/), there's one Rabbit MQ setting you
really want to know about before your first broker outage, and that's the quorum queue *delivery limit*.

Starting with Rabbit MQ 4, every quorum queue has a delivery limit of 20 by default. Every time a message goes
back to the queue without being acknowledged, the broker counts it, and on the 21st return the broker dead letters
the message -- or just drops it on the floor if the queue doesn't have a dead letter exchange. That sounds like a
reasonable poison message guard, and for a lot of systems it is. But think about what actually hands a message back
to the queue:

* A listener that is stopping, pausing, or restarting with messages still prefetched in memory
* A channel or connection that closes during a network blip or a broker restart
* A reject with requeue, which counts on every Rabbit MQ 4 version we've tried
* A nack with requeue -- at least on Rabbit MQ 4.2.5, where a reject, a nack and a closed channel all counted against
  the limit the exact same way. Newer 4.x releases stopped counting the nack, and Wolverine now hands transient
  deliveries back with a nack for exactly that reason, but you shouldn't bet your messages on which broker version
  you happen to be running

None of those mean your message is poison. With the durable inbox, Wolverine acknowledges a message to Rabbit MQ
just as soon as it's safely stored in your database, and from then on Wolverine's own [error handling
policies](/guide/handlers/error-handling) decide what happens to a message that keeps failing. So the only thing the
broker's delivery limit is really counting for a durable listener is how many times your system was disrupted while
that message was in flight. A long enough outage with enough reconnects, and you can lose a perfectly good message
without a single exception in your logs.

To get out in front of that, you can opt into an `x-delivery-limit` for the quorum queues that Wolverine declares,
either for every durable listener at once or queue by queue:

<!-- snippet: sample_delivery_limit_for_durable_quorum_queues -->
<a id='snippet-sample_delivery_limit_for_durable_quorum_queues'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    opts
        .UseRabbitMq(builder.Configuration.GetConnectionString("rabbit")!)
        .AutoProvision()
        .UseQuorumQueues()

        // Every quorum queue that Wolverine declares for a listener
        // using the durable inbox gets this x-delivery-limit.
        // -1 turns the limit off on RabbitMQ 4
        .DeliveryLimitForDurableQuorumQueues(-1);

    opts.ListenToRabbitQueue("incoming")
        .UseDurableInbox();

    opts.ListenToRabbitQueue("payments")
        .UseDurableInbox()

        // Or set the limit for just one queue, and this
        // wins over the transport wide setting
        .DeliveryLimit(100)

        // Optional, but this lets in flight deliveries reach
        // the inbox on a stop instead of being handed back
        .DrainWaitForPrefetch();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L599-L628' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_delivery_limit_for_durable_quorum_queues' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

A couple of things to know:

* Neither setting changes anything unless you opt in. Wolverine declares your queues exactly the way it always has
  by default.
* `DeliveryLimitForDurableQuorumQueues()` only applies to quorum queues that are listened to with the durable inbox.
  Inline or buffered listeners don't have the inbox to fall back on, so the broker's limit is still doing real work
  for them.
* The limit is only applied when Wolverine is the one declaring the queue. If you mark a queue as externally owned or
  let some other process create it, Wolverine stays out of it.
* `-1` turns the limit off on Rabbit MQ 4. If you'd rather keep a backstop, pick a number big enough to ride out
  your worst outage.

::: warning
Rabbit MQ will not let you redeclare an existing queue with different arguments, so adding a delivery limit to a
queue that already exists will make Wolverine fail at startup with a "inequivalent arg" error. For an existing queue,
either delete the queue and let Wolverine recreate it, or set the delivery limit with a Rabbit MQ
[policy](https://www.rabbitmq.com/docs/parameters#policies) instead, which applies to existing queues without
redeclaring anything.
:::

::: tip
If Wolverine declares a quorum queue for a durable listener without any delivery limit, you'll see a warning in your
logs when that listener starts up. That's your cue to either opt in like the sample above, or confirm that a Rabbit
MQ policy is taking care of it for you.
:::

## Inside of Wolverine Extensions

If you need to declare Rabbit MQ queues, exchanges, or bindings within a [Wolverine extension](/guide/extensions),
you can quickly access and make additions to the Rabbit MQ integration with your Wolverine application
like so:

<!-- snippet: sample_rabbitmq_configuration_in_wolverine_extension -->
<a id='snippet-sample_rabbitmq_configuration_in_wolverine_extension'></a>
```cs
public class MyModuleExtension : IWolverineExtension
{
    public void Configure(WolverineOptions options)
    {
        options.ConfigureRabbitMq()
            // Make any Rabbit Mq configuration or declare
            // additional Rabbit Mq options through the normal
            // syntax
            .DeclareExchange("my-module")
            .DeclareQueue("my-queue");
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L641-L655' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_rabbitmq_configuration_in_wolverine_extension' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Identifier Prefixing for Shared Brokers

Because Rabbit MQ is a centralized broker model, you may need to share a single broker between multiple developers or development environments. To isolate the broker objects (queues, exchanges, bindings) created by each environment, you can use the `PrefixIdentifiers()` method to automatically prepend a prefix to every queue and exchange name created by Wolverine:

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRabbitMq()
            .AutoProvision()

            // Prefix all queue and exchange names with "dev-john-"
            .PrefixIdentifiers("dev-john");

        // A queue named "orders" becomes "dev-john-orders"
        opts.ListenToRabbitQueue("orders");
    }).StartAsync();
```

You can also use `PrefixIdentifiersWithMachineName()` as a convenience to use the current machine name as the prefix, which is often a good default for local development:

```csharp
opts.UseRabbitMq()
    .AutoProvision()
    .PrefixIdentifiersWithMachineName();
```

The default delimiter between the prefix and the original name is `-` for Rabbit MQ (e.g., `dev-john-orders`).
