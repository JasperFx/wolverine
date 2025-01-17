# Modular Monoliths

::: info
Wolverine's mantra is "low code ceremony," and the modular monolith approach comes with a mountain of temptation
for a certain kind of software architect to inject a world of potentially harmful high ceremony coding techniques.
The Wolverine team urges you to proceed with caution and allow simplicity to trump architectural theories about coupling between
application modules.
:::

Most of use are unhappy with the longer term effects of building the giant monolithic systems of yore. We got systems with slow
build times that made our IDE tools sluggish and were frequently hard to maintain over time. Formal prescriptive architectural
approaches like the Clean/Onion/Hexagonal Architecture mostly just worry about layering by technical concerns and don't really
do anything effective to make a large application maintainable over time. 

Enter micro-services as an attempt to build software in smaller chunks where you might be able to be mostly working on smaller
codebases with quicker builds, faster tests, and a much easier time upgrading technical infrastructure compared to monolithic applications. 
Of course there were some massive downsides with the whole distributed development thing, and our industry became disillusioned.

While micro-services as a concept might be parked in the [trough of despair](https://tidyfirst.substack.com/p/the-trough-of-despair),
the new thinking is to use a so called "Modular Monolith" approach is attractive to a lot of folks as a way to have the best of 
both worlds. Start inside a single process, but try to create more vertical decoupling between logical modules in the system
as an alternative to both monoliths and micro-services. 

![Modular Monolith](/modular-monolith.png)

Borrowing heavily from [Milan Jovanović's writing on Modular Monoliths](https://www.milanjovanovic.tech/blog/what-is-a-modular-monolith), the potential benefits are:

* Easier deployments than micro-services from simply having less to deploy
* Improved performance assuming that integration between modules is done in process
* Maybe easier debugging by just having one process to deal with, but asynchronous messaging even in process is never going to be the easiest thing in the world
* Hopefully, you have a relatively easy path to being able to separate modules into separate services later as the logical boundaries become clear. Arguably some of the worst
  outcomes of micro-services come from getting the service boundaries wrong upfront and creating very chatty interactions between different services. That can still
  happen with a modular monolith, but hopefully it's a lot easier to correct the boundaries later. We'll talk a lot more about this in the "Severability" section.
* The ability to adjust transaction boundaries to use native database transactions as it's valuable instead of only having eventual consistency



## Important Wolverine Settings 

Wolverine was admittedly conceived of and optimized for a world where Micro-Service architectures was the hot topic, and 
we've had to scramble a little bit as a community to make Wolverine be more suitable for how users want to use Wolverine
for modular monoliths. To avoid making breaking changes, we've put some Modular Monolith-friendly features behind configuration
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

Do note that Wolverine identifies message types by default by the message type's full Type name. You can always override
that explicitly through the [`[MessageIdentity]`](/guide/messages.html#message-type-name-or-alias) attribute, but you might
try to *not* have to move message types around in the namespace structure. The only real impact is on messages that are in flight
in either external message queues or message persistence. 

For handler or HTTP endpoint discovery, you can tell Wolverine to look in additional assemblies. See [Assembly Discovery](/guide/messages.html#message-type-name-or-alias)
for more information. 

## In Process vs External Messaging

::: tip
Just to be clear, we pretty well never recommend calling `IMessageBus.InvokeAsync()` inline in any message handler to another message handler. For the most part,
we think you can build much more robust and resilient systems by leveraging asynchronous messaging. Using [Wolverine as a "Mediator"](/tutorials/mediator) in MVC controllers, Minimal API functions,
or maybe Hot Chocolate mutations is an exception case that we fully support. 
:::

You can communicate  do any mix of in process messaging and messaging through external messaging brokers like Rabbit MQ or Azure Service Bus.
Let's say you do have MORE HERE

## Eventual Consistency between Modules

We (the Wolverine team) are loathe to recommend using [eventual consistency](https://en.wikipedia.org/wiki/Eventual_consistency) between modules if you don't have to. It's always
going to be technically simpler to just make all the related changes in a single database transaction. It'll definitely
be easier to test and troubleshoot problems if you don't use eventual consistency. Not to mention the challenges with 
user interfaces getting the right updates and possibly dealing with stale data.

All that being said, there are absolutely good use cases for eventual consistency and Wolverine makes that quite simple.


## With EF Core

## With Marten

[Marten](https://martendb.io) plays pretty well with modular monoliths. For the most part, you can happily 

## Observability

If you're going to opt into using asynchronous message passing within your application between modules or even just really
using any kind of asynchronous messaging within a Wolverine application, we very strongly recommending using some
sort of [OpenTelemetry](https://opentelemetry.io/) (Otel) compatible monitoring tool (I would think that every monitoring tool supports Otel by now).
Wolverine emits Otel activity spans for all message processing as well as just about any kind of relevant event within a
Wolverine application.

See [the Wolverine Otel support](/guide/logging.html#open-telemetry) for more information.
