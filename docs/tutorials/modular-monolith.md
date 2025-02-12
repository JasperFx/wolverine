# Modular Monoliths

::: info
Wolverine's mantra is "low code ceremony," and the modular monolith approach comes with a mountain of temptation
for a certain kind of software architect to try out a world of potentially harmful high ceremony coding techniques.
The Wolverine team urges you to proceed with caution and allow simplicity to trump architectural theories about coupling between
application modules. 
:::

Software development is still a young profession, and we are still figuring out the best ways to build systems, and that means
the pendulum swings a bit back and forth on what the software community thinks is the best way to build large systems. We saw
some poor results from the old monolithic applications of your as we got codebases with slow
build times that made our IDE tools sluggish and were generally just hard to maintain over time. 

Enter micro-services as an attempt to build software in smaller chunks where you might be able to be mostly working on smaller
codebases with quicker builds, faster tests, and a much easier time upgrading technical infrastructure compared to monolithic applications. 
Of course there were some massive downsides with the whole distributed development thing, and our industry has become disillusioned.

::: tip
We still think that Wolverine (and Marten) with its relentless focus on low ceremony code and strong support for asynchronous
messaging makes the "Critter Stack" a great fit for micro-services -- and in some sense, a "modular monolith" can also be
the first stage of a system architecture that ends up being micro-services after the best service boundaries are proven
out *before* you try to pull modules into a separate service. 
:::

While micro-services as a concept might be parked in the [trough of despair](https://tidyfirst.substack.com/p/the-trough-of-despair) for awhile,
the new thinking is to use a so called "Modular Monolith" approach that splits the difference between monoliths and micro-services.  
The general idea is to start inside of a single process, but try to create more vertical decoupling between logical modules in the system
as an alternative to both monoliths and micro-services. 

![Modular Monolith](/modular-monolith.png)

The hope is that you can more easily reason about the code in a single
module at a time compared to a monolith, but without having to tackle the extra deployment and management of micro-services
upfront. Borrowing heavily from [Milan Jovanović's writing on Modular Monoliths](https://www.milanjovanovic.tech/blog/what-is-a-modular-monolith), the potential benefits are:

* Easier deployments than micro-services from simply having less to deploy
* Improved performance assuming that integration between modules is done in process
* Maybe easier debugging by just having one process to deal with, but asynchronous messaging even in process is never going to be the easiest thing in the world
* Hopefully, you have a relatively easy path to being able to separate modules into separate services later as the logical boundaries become clear. Arguably some of the worst
  outcomes of micro-services come from getting the service boundaries wrong upfront and creating very chatty interactions between different services. That can still
  happen with a modular monolith, but hopefully it's a lot easier to correct the boundaries later. We'll talk a lot more about this in the "Severability" section.
* The ability to adjust transaction boundaries to use native database transactions as it's valuable instead of only having eventual consistency

Another explicitly stated hope for modular monoliths is that you're able to better iterate between modules to find the most
effective boundaries between logical modules *before* severing modules into separate services later when that is beneficial.

## Important Wolverine Settings 

Wolverine was admittedly conceived of and optimized for a world where micro-service architecture was the hot topic, and 
we've had to scramble a little bit as a community lately to make Wolverine be more suitable for how users now want to use Wolverine
for modular monoliths. To avoid making breaking changes, we've had to put some modular monolith-friendly features behind configuration
settings so as not to break existing users.

Specifically, Wolverine "classic" has two conceptual problems for modular monoliths with its original model:

1. If you have multiple message handlers for the same message type, Wolverine combines these handlers into one logical
   message handler and one logical transaction
2. Messages in its [transactional inbox](/guide/durability/#using-the-inbox-for-incoming-messages) are identified by only the message id. That's worked great until folks start wanting to receive the same message from an external broker, but handled
   separately by different handlers receiving the same message from different queues or subscriptions or topics depending on the external transport. This is shown below:

![Receiving Same Message 2 or More Times](/receive-message-twice.png)

Both of these behaviors can be changed in your application by setting these two flags shown below:

<!-- snippet: sample_important_settings_for_modular_monoliths -->
<a id='snippet-sample_important_settings_for_modular_monoliths'></a>
```cs
var builder = Host.CreateApplicationBuilder();

// It's not important that it's Marten here, just that if you have
// *any* message persistence configured for the transactional inbox/outbox
// support, it's impacted by the MessageIdentity setting
builder.Services.AddMarten(opts =>
    {
        var connectionString = builder.Configuration.GetConnectionString("marten");
        opts.Connection(connectionString);
    })
    
    // This line of code is adding a PostgreSQL backed transactional inbox/outbox 
    // integration using the same database as what Marten is using
    .IntegrateWithWolverine();

builder.UseWolverine(opts =>
{
    // Tell Wolverine that when you have more than one handler for the same
    // message type, they should be executed separately and automatically
    // "stuck" to separate local queues
    opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

    // *If* you may be using external message brokers in such a way that they
    // are "fanning out" a single message sent from an upstream system into
    // multiple listeners within the same Wolverine process, you'll want to make
    // this setting to tell Wolverine to treat that as completely different messages
    // instead of failing by idempotency checks
    opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;
    
    // Not 100% necessary for "modular monoliths", but this makes the Wolverine durable
    // inbox/outbox feature a lot easier to use and DRYs up your message handlers
    opts.Policies.AutoApplyTransactions();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L234-L271' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_important_settings_for_modular_monoliths' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

See [Message Identity](/guide/durability/#message-identity) and [Multiple Handlers for the Same Message Type](/guide/handlers/#multiple-handlers-for-the-same-message-type)
for more detail.

The `MultipleHandlerBehavior.Separated` setting is meant for an increasingly common scenario shown below where you want
to take completely separate actions on an event message by separate logical modules published by an upstream handler:

![Publishing a message to multiple local subscribers](/publish-event-to-multiple-handlers.png)

By using the `MultipleHandlerBehavior.Separated` setting, we're directing Wolverine to track any `OrderPlaced` event message
completely separately for each handler. By default this would be publishing the event message to two completely separate
[local, in process queues](/guide/messaging/transports/local) inside the Wolverine application. By publishing to separate queues, 
you also get:

* Independent transactions for each handler -- assuming that you're using Wolverine transactional middleware anyway
* Separate retry loops and potentially different [error handling policies](/guide/handlers/error-handling) for the message to each handler
* The ability to mix and match [durable vs lighter weight "fire and forget"](/guide/runtime.html#endpoint-types) (`Buffered` in Wolverine parlance) semantics for different handlers
* Granular tracing and logging on the handlers


## Splitting Your System into Separate Assemblies

::: info
The technical leader of Wolverine has a decades old loathing of the Onion Architecture and now the current Clean Architecture
fad. While it's perfectly possible to spread a Wolverine application out over separate assemblies, we'd urge you to 
keep your project structure as simple as possible and not automatically incur extra code ceremony by trying to use separate
projects just to enforce coupling rules.
:::

To be honest, the Wolverine team would recommend just keeping your modules segregated in separate namespaces until the initial
system gets subjectively big enough that you'd want them separated. 

Do note that Wolverine identifies message types by default by the message type's full type name ([.NET namespace].[type name]). You can always override
that explicitly through the [`[MessageIdentity]`](/guide/messages.html#message-type-name-or-alias) attribute, but you might
try to *not* have to move message types around in the namespace structure. The only real impact is on messages that are in flight
in either external message queues or message persistence, so it does no harm to change namespaces if you are only in development and have not
yet deployed to production. 

For handler or HTTP endpoint discovery, you can tell Wolverine to look in additional assemblies. See [Assembly Discovery](/guide/messages.html#message-type-name-or-alias)
for more information. As for [pre-generated code](/guide/codegen) with Wolverine, the least friction and idiomatic approach is to just have
all Wolverine-generated code placed in the entry assembly. That can be overridden if you have to by setting the "Application Assembly" as shown
in the [Assembly Discovery](/guide/handlers/discovery.html#assembly-discovery) section in the documentation.

## In Process vs External Messaging

::: tip
Just to be clear, we pretty well never recommend calling `IMessageBus.InvokeAsync()` inline in any message handler to another message handler. For the most part,
we think you can build much more robust and resilient systems by leveraging asynchronous messaging. Using [Wolverine as a "Mediator"](/tutorials/mediator) in MVC controllers, Minimal API functions,
or maybe Hot Chocolate mutations is an exception case that we fully support. We think this advice applies to any mediator tool and the pattern
in general as well. 
:::

By and large, the Wolverine community will recommend you do most communication between modules through some sort of asynchronous
messaging, either locally in process or through external message brokers. Asynchronous messaging will help you keep your modules
decoupled, and often leads to much more resilient systems as your modules aren't "temporally" coupled and you utilize
[retry or other error handling policies](/guide/handlers/error-handling) independently on downstream queues.

You can communicate  do any mix of in process messaging and messaging through external messaging brokers like Rabbit MQ or Azure Service Bus.
Let's start with just using local, in process queueing with Wolverine between your modules as shown below:

![Communicating through local queues](/modular-monolith-local-queues.png)

Now, let's say that you want to publish an `OrderPlaced` event message from the successful processing of a `PlaceOrder`
command in a message handler something like this:

```csharp
public static OrderPlaced Handle(PlaceOrder command)
{
    // actually do stuff to place a new order...
    
    // Returning this from the method will "cascade" this
    // object as a message. Essentially just publishing
    // this as a message to any active subscribers in the
    // Wolverine system
    return new OrderPlaced(command.OrderId);
}
```

and assuming that there's *at least one* known message handler in your application for the `OrderPlaced` event:

```csharp
public static class OrderPlacedHandler
{
    public static void Handle(OrderPlaced @event) 
        => Debug.WriteLine("got a new order " + @event.OrderId);
}
```

then Wolverine -- by default -- will happily publish `OrderPlaced` through [a local queue](/guide/messaging/transports/local) named after the full type name
of the `OrderPlaced` event. You can even make these local queues durable by having them effectively backed by your application's
Wolverine message storage (the transactional inbox to be precise), with a couple different approaches to do this shown below:

<!-- snippet: sample_durable_local_queues -->
<a id='snippet-sample_durable_local_queues'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Policies.UseDurableLocalQueues();

        // or

        opts.LocalQueue("important").UseDurableInbox();

        // or conventionally, make the local queues for messages in a certain namespace
        // be durable
        opts.Policies.ConfigureConventionalLocalRouting().CustomizeQueues((type, queue) =>
        {
            if (type.IsInNamespace("MyApp.Commands.Durable"))
            {
                queue.UseDurableInbox();
            }
        });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L106-L128' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_durable_local_queues' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Using local queues for communication is a simple way to get started, requires less deployment overhead in general, and is potentially
faster than using external message brokers due to the in process communication.

::: info
If you are using durable local queues, Wolverine is still serializing the message to put it in the durable transactional inbox storage,
but the actual message object is used as is when it's passed into the local queue.
:::

Alternatively, you could instead choose to do all intra-module communication through external message brokers as shown below:

![Communicating through external brokers](/modular-monolith-communication-external-broker.png)

Picking Azure Service Bus for our sample, you could use conventional message routing to publish all messages through your system
through Azure Service Bus queues like this:

<!-- snippet: sample_using_conventional_broker_routing_with_local_routing_turned_off -->
<a id='snippet-sample_using_conventional_broker_routing_with_local_routing_turned_off'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    // Turn *off* the conventional local routing so that
    // the messages that this application handles still go
    // through the external Azure Service Bus broker
    opts.Policies.DisableConventionalLocalRouting();
    
    // One way or another, you're probably pulling the Azure Service Bus
    // connection string out of configuration
    var azureServiceBusConnectionString = builder
        .Configuration
        .GetConnectionString("azure-service-bus");

    // Connect to the broker in the simplest possible way
    opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision()
        .UseConventionalRouting();
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L370-L394' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_conventional_broker_routing_with_local_routing_turned_off' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

By using external queues instead of local queues, you are:

* Potentially getting smoother load balanced workloads between running nodes of a clustered application
* Reducing memory pressure in your applications, especially if there's any risk of a queue getting backed up and growing large in memory

And of course, Wolverine has a wealth of ways to customize message routing for sequencing, grouping, and parallelization. As well
as allowing you to mix and match local and external broker messaging or durable and non-durable messaging all within the same application.

See the recently updated documentation on [Message Routing in Wolverine](/guide/messaging/subscriptions) to learn more. 

## Eventual Consistency between Modules

We (the Wolverine team) are loathe to recommend using [eventual consistency](https://en.wikipedia.org/wiki/Eventual_consistency) between modules if you don't have to. It's always
going to be technically simpler to just make all the related changes in a single database transaction. It'll definitely
be easier to test and troubleshoot problems if you don't use eventual consistency. Not to mention the challenges with 
user interfaces getting the right updates and possibly dealing with stale data.

**To be clear though, we strongly recommend using asynchronous communication between modules** and recommend against 
using `IMessageBus.InvokeAsync()` inline in most cases to synchronously interact with any other module from a message handler. We think
your most common decision is:

* Would it be easier in the end to combine functionality into one larger module to utilize transactional integrity and avoid the need for eventual
consistency through asynchronous messaging
* Or is there a real justification for publishing event messages to other modules to take action later?

Assuming that you do opt for eventual consistency, Wolverine makes that quite simple. Just make
sure that you are using [durable endpoints](/guide/durability) for communication between any two or more actions that are involved for the implied
eventual consistency transactional boundary so that the work does not get lost even in the face of transient errors or unexpected
system shutdowns.

::: tip
Look, MediatR is an almost dominant tool in the .NET ecosystem right now, but it doesn't come with any kind of built
in transactional inbox/outbox support that you need to make asynchronous message passing be resilient. See [MediatR to Wolverine](/tutorials/from-mediatr)
for information about switching to Wolverine from MediatR.
:::

## Test Automation Support

::: info
As a community, we'll most assuredly need to add more convenient API signatures to the tracked sessions specifically
to deal with the new usages coming out of modular monolith strategies, but we're first waiting for feedback from real projects on what
would be helpful before doing that.  
:::

Wolverine's [Tracked Sessions](/guide/testing.html#integration-testing-with-tracked-sessions) feature is purpose built
for test automation support when you want to write tests that might span the activity of more than one message being handled.
Consider the case of testing the handling of a `PlaceOrder` command that in turn publishes an `OrderPlaced` event message
that is handled by one or more other handlers within your modular monolith system. If you want to write a **reliable**
test that spans the activities of all of these messages, you can utilize Wolverine's "tracked sessions" like this:

<!-- snippet: sample_using_tracked_sessions_end_to_end -->
<a id='snippet-sample_using_tracked_sessions_end_to_end'></a>
```cs
// Personally, I prefer to reuse the IHost between tests and
// do something to clear off any dirty state, but other folks
// will spin up an IHost per test to maybe get better test parallelization
public static async Task run_end_to_end(IHost host)
{
    var placeOrder = new PlaceOrder("111", "222", 1000);
    
    // This would be the "act" part of your arrange/act/assert
    // test structure
    var tracked = await host.InvokeMessageAndWaitAsync(placeOrder);
    
    // proceed to test the outcome of handling the original command *and*
    // any subsequent domain events that are published from the original
    // command handler
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/DocumentationSamples.cs#L34-L52' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_tracked_sessions_end_to_end' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the code sample above, the `InvokeAndMessageAndWaitAsync()` method puts the Wolverine runtime into a "tracked" mode
where it's able to "know" when all in flight work is complete and allow your integration testing to be reliable by
waiting until all cascaded messages are also complete (and yes, it works recursively). One of the challenges of testing
asynchronous code is not doing the *assert* phase of the test until the *act* part is really complete, and "tracked sessions"
are Wolverine's answer to that problem.

Just to note that there are more options you'll maybe need to use with modular monoliths, this version of 
tracking activity also includes any outstanding work from messages that are sent to external brokers:

<!-- snippet: sample_using_external_brokers_with_tracked_sessions -->
<a id='snippet-sample_using_external_brokers_with_tracked_sessions'></a>
```cs
public static async Task run_end_to_end_with_external_transports(IHost host)
{
    var placeOrder = new PlaceOrder("111", "222", 1000);
    
    // This would be the "act" part of your arrange/act/assert
    // test structure
    var tracked = await host
        .TrackActivity()
        
        // Direct Wolverine to also track activity coming and going from
        // external brokers
        .IncludeExternalTransports()
        
        // You'll sadly need to do this sometimes
        .Timeout(30.Seconds())
        
        // You *might* have to do this as well to make
        // your tests more reliable in the face of async messaging
        .WaitForMessageToBeReceivedAt<OrderPlaced>(host)
        
        .InvokeMessageAndWaitAsync(placeOrder);
    
    // proceed to test the outcome of handling the original command *and*
    // any subsequent domain events that are published from the original
    // command handler
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/DocumentationSamples.cs#L66-L95' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_external_brokers_with_tracked_sessions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And to test the invocation of an event message to a specific handler, we can still do that by sending the message to a specific local queue:

<!-- snippet: sample_test_specific_queue_end_to_end -->
<a id='snippet-sample_test_specific_queue_end_to_end'></a>
```cs
public static async Task test_specific_handler(IHost host)
{
    // We're not thrilled with this usage and it's possible there's
    // syntactic sugar additions to the API soon
    await host.ExecuteAndWaitAsync(
        c => c.EndpointFor("local queue name").SendAsync(new OrderPlaced("111")).AsTask());
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/DocumentationSamples.cs#L54-L64' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_test_specific_queue_end_to_end' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## With EF Core

::: tip
There is no way to utilize more than one `DbContext` type in a single handler while using the Wolverine transactional middleware.
You can certainly do that, just with explicit code. 
:::

For EF Core usage, we would recommend using separate `DbContext` types for different modules that all target a separate
database schema, but still land in the same physical database. This may change soon, but for right now, Wolverine only
supports transactional inbox/outbox usage with a single database with EF Core.

To maintain "severability" of modules to separate services later, you probably want to avoid making foreign key relationships
in your database between tables owned by different modules. And of course, by and large only use one `DbContext` type
in the code for a single module. Or maybe more accurately, only one module should use one `DbContext`.

## With Marten

[Marten](https://martendb.io) plays pretty well with modular monoliths. For the most part, you can happily just stick all your documents
in the same database schema and use the same `IDocumentStore` if you want while still being able to migrate some of those documents 
later if you choose to sever some modules over into a separate service. With the event sourcing though, all the events for 
different aggregate types or stream types all go into the same events table. While it's not impossible to separate the events
through database scripts if you want to move a module into a separate service later, it's probably going to be easier
if you use [Marten's separate document store](https://martendb.io/configuration/hostbuilder.html#working-with-multiple-marten-databases) feature. 

Wolverine has [direct support for Marten's separate or "ancillary" stores](/guide/durability/marten/ancillary-stores) that still enables the usage of all Wolverine + Marten
integrations. There is currently a limitation that there is only one physical database that shares the Wolverine message storage for
the transactional inbox/outbox across all modules.

Also note that the Wolverine + Marten "Critter Stack" combination is a great fit for "Event Driven Architecture" approaches
where you depend on reliably publishing event messages to interested listeners in your application -- which is essentially 
how a lot of folks want to build their modular monoliths. 

See the introduction to [event subscriptions from Marten](/tutorials/cqrs-with-marten.html#publishing-or-handling-events). 

Do note that if you are using multiple document stores with Marten for different modules, but all the stores target the 
exact same physical PostgreSQL database as shown in this diagram below:

![Modules using the same physical database](/modules-hitting-same-database.png)

You can help Wolverine be a little more efficient by using the same transactional inbox/outbox storage across all modules
by using this setting:

<!-- snippet: sample_using_message_storage_schema_name -->
<a id='snippet-sample_using_message_storage_schema_name'></a>
```cs
// THIS IS IMPORTANT FOR MODULAR MONOLITH USAGE!
opts.Durability.MessageStorageSchemaName = "wolverine";
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/AncillaryStores/bootstrapping_ancillary_marten_stores_with_wolverine.cs#L59-L64' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_message_storage_schema_name' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

By setting any value for `WolverineOptions.Durability.MessageStorageSchemaName`, Wolverine will use that value for the database schema
of the message storage tables, and be able to share the inbox/outbox processing across all the modules.

## Observability

If you're going to opt into using asynchronous message passing within your application between modules or even just really
using any kind of asynchronous messaging within a Wolverine application, we very strongly recommending using some
sort of [OpenTelemetry](https://opentelemetry.io/) (Otel) compatible monitoring tool (I would think that every monitoring tool supports Otel by now).
Wolverine emits Otel activity spans for all message processing as well as just about any kind of relevant event within a
Wolverine application.

See [the Wolverine Otel support](/guide/logging.html#open-telemetry) for more information.


