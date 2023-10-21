# Marten Integration

[Marten](https://martendb.io) and Wolverine are sibling projects under the [JasperFx organization](https://github.com/wolverinefx), and as such, have quite a bit of synergy when
used together. At this point, adding the `WolverineFx.Marten` Nuget dependency to your application adds the capability to combine Marten and Wolverine to:

* Simplify persistent handler coding with transactional middleware
* Use Marten and Postgresql as a persistent inbox or outbox with Wolverine messaging
* Support persistent sagas within Wolverine applications
* Effectively use Wolverine and Marten together for a [Decider](https://thinkbeforecoding.com/post/2021/12/17/functional-event-sourcing-decider) function workflow with event sourcing
* Selectively publish events captured by Marten through Wolverine messaging

## Getting Started

To use the Wolverine integration with Marten, just install the Wolverine.Persistence.Marten Nuget into your application. Assuming that you've [configured Marten](https://martendb.io/configuration/)
in your application (and Wolverine itself!), you next need to add the Wolverine integration to Marten as shown in this sample application bootstrapping:

<!-- snippet: sample_integrating_wolverine_with_marten -->
<a id='snippet-sample_integrating_wolverine_with_marten'></a>
```cs
var builder = WebApplication.CreateBuilder(args);
builder.Host.ApplyOaktonExtensions();

builder.Services.AddMarten(opts =>
    {
        var connectionString = builder
            .Configuration
            .GetConnectionString("postgres");

        opts.Connection(connectionString);
        opts.DatabaseSchemaName = "orders";
    })
    // Optionally add Marten/Postgresql integration
    // with Wolverine's outbox
    .IntegrateWithWolverine();

// You can also place the Wolverine database objects
// into a different database schema, in this case
// named "wolverine_messages"
//.IntegrateWithWolverine("wolverine_messages");

builder.Host.UseWolverine(opts =>
{
    // I've added persistent inbox
    // behavior to the "important"
    // local queue
    opts.LocalQueue("important")
        .UseDurableInbox();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WebApiWithMarten/Program.cs#L8-L40' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_integrating_wolverine_with_marten' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

For more information, see [durable messaging](/guide/durability/) and the [sample Marten + Wolverine project](https://github.com/JasperFx/wolverine/tree/main/src/Samples/WebApiWithMarten).

Using the `IntegrateWithWolverine()` extension method behind your call to `AddMarten()` will:

* Register the necessary [inbox and outbox](/guide/durability/) database tables with [Marten's database schema management](https://martendb.io/schema/migrations.html)
* Adds Wolverine's "DurabilityAgent" to your .NET application for the inbox and outbox
* Makes Marten the active [saga storage](/guide/durability/sagas) for Wolverine
* Adds transactional middleware using Marten to your Wolverine application


## Marten as Outbox

::: tip
Wolverine's outbox will help you order all outgoing messages until after the database transaction succeeds, but only messages being delivered
to endpoints explicitly configured to be persistent will be stored in the database. While this may add complexity, it does give you fine grained
support to mix and match fire and forget messaging with messages that require durable persistence.
:::

One of the most important features in all of Wolverine is the [persistent outbox](https://microservices.io/patterns/data/transactional-outbox.html) support and its easy integration into Marten.
If you're already familiar with the concept of an "outbox" (or "inbox"), skip to the sample code below.

Here's a common problem when using any kind of messaging strategy. Inside the handling for a single web request, you need to make some immediate writes to
the backing database for the application, then send a corresponding message out through your asynchronous messaging infrastructure. Easy enough, but here's a few ways
that could go wrong if you're not careful:

* The message is received and processed before the initial database writes are committed, and you get erroneous results because of that (I've seen this happen)
* The database transaction fails, but the message was still sent out, and you get inconsistency in the system
* The database transaction succeeds, but the message infrastructure fails some how, so you get inconsistency in the system

You could attempt to use some sort of [two phase commit](https://martinfowler.com/articles/patterns-of-distributed-systems/two-phase-commit.html)
between your database and the messaging infrastructure, but that has historically been problematic. This is where the "outbox" pattern comes into play to guarantee
that the outgoing message and database transaction both succeed or fail, and that the message is only sent out after the database transaction has succeeded.

Imagine a simple example where a Wolverine handler is receiving a `CreateOrder` command that will span a brand new Marten `Order` document and also publish
an `OrderCreated` event through Wolverine messaging. Using the outbox, that handler **in explicit, long hand form** is this:

<!-- snippet: sample_longhand_order_handler -->
<a id='snippet-sample_longhand_order_handler'></a>
```cs
public static async Task Handle(
    CreateOrder command,
    IDocumentSession session,
    IMartenOutbox outbox,
    CancellationToken cancellation)
{
    var order = new Order
    {
        Description = command.Description
    };

    // Register the new document with Marten
    session.Store(order);

    // Hold on though, this message isn't actually sent
    // until the Marten session is committed
    await outbox.SendAsync(new OrderCreated(order.Id));

    // This makes the database commits, *then* flushed the
    // previously registered messages to Wolverine's sending
    // agents
    await session.SaveChangesAsync(cancellation);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WebApiWithMarten/Order.cs#L104-L130' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_longhand_order_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the code above, the `OrderCreated` message is registered with the Wolverine `IMessageContext` for the current message, but nothing more than that is actually happening at that point.
When `IDocumentSession.SaveChangesAsync()` is called, Marten is persisting the new `Order` document **and** creating database records for the outgoing `OrderCreated` message
in the same transaction (and even in the same batched database command for maximum efficiency). After the database transaction succeeds, the pending messages are automatically sent to Wolverine's
sending agents.

Now, let's play "what if:"

* What if the messaging broker is down? As long as the messages are persisted, Wolverine will continue trying to send the persisted outgoing messages until the messaging broker is back up and available.
* What if the application magically dies after the database transaction but before the messages are sent through the messaging broker? Wolverine will still be able to send these persisted messages from
  either another running application node or after the application is restarted.

The point here is that Wolverine is doing store and forward mechanics with the outgoing messages and these messages will eventually be sent to the messaging infrastructure (unless they hit a designated expiration that you've defined).

In the section below on transactional middleware we'll see a shorthand way to simplify the code sample above and remove some repetitive ceremony.

## Outbox with ASP.Net Core

The Wolverine outbox is also usable from within ASP.Net Core (really any code) controller or Minimal API handler code. Within an MVC controller, the `CreateOrder`
handling code would be:

<!-- snippet: sample_CreateOrderController -->
<a id='snippet-sample_createordercontroller'></a>
```cs
public class CreateOrderController : ControllerBase
{
    [HttpPost("/orders/create2")]
    public async Task Create(
        [FromBody] CreateOrder command,
        [FromServices] IDocumentSession session,
        [FromServices] IMartenOutbox outbox)
    {
        var order = new Order
        {
            Description = command.Description
        };

        // Register the new document with Marten
        session.Store(order);

        // Don't worry, this message doesn't go out until
        // after the Marten transaction succeeds
        await outbox.PublishAsync(new OrderCreated(order.Id));

        // Commit the Marten transaction
        await session.SaveChangesAsync();
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WebApiWithMarten/Order.cs#L20-L47' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_createordercontroller' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

From a Minimal API, that could be this:

<!-- snippet: sample_create_order_through_minimal_api -->
<a id='snippet-sample_create_order_through_minimal_api'></a>
```cs
app.MapPost("/orders/create3", async (CreateOrder command, IDocumentSession session, IMartenOutbox outbox) =>
{
    var order = new Order
    {
        Description = command.Description
    };

    // Register the new document with Marten
    session.Store(order);

    // Don't worry, this message doesn't go out until
    // after the Marten transaction succeeds
    await outbox.PublishAsync(new OrderCreated(order.Id));

    // Commit the Marten transaction
    await session.SaveChangesAsync();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WebApiWithMarten/Program.cs#L62-L82' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_create_order_through_minimal_api' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->



## Transactional Middleware

::: warning
When using the transactional middleware with Marten, Wolverine is assuming that there will be a single, 
atomic transaction for the entire message handler. Because of the integration with Wolverine's outbox and 
the Marten `IDocumentSession`, it is **very strongly** recommended that you do not call `IDocumentSession.SaveChangesAsync()`
yourself as that may result in unexpected behavior in terms of outgoing messages.
:::

::: tip
You will need to make the `IServiceCollection.AddMarten(...).IntegrateWithWolverine()` call to add this middleware to a Wolverine application.
:::

It is no longer necessary to mark a handler method with `[Transactional]` if you choose to use the `AutoApplyTransactions()` option as shown below: 

<!-- snippet: sample_using_auto_apply_transactions_with_marten -->
<a id='snippet-sample_using_auto_apply_transactions_with_marten'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Services.AddMarten("some connection string")
            .IntegrateWithWolverine();

        // Opt into using "auto" transaction middleware
        opts.Policies.AutoApplyTransactions();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Marten/Sample/BootstrapWithAutoTransactions.cs#L12-L24' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_auto_apply_transactions_with_marten' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

With this enabled, Wolverine will automatically use the Marten
transactional middleware for handlers that have a dependency on `IDocumentSession` (meaning the method takes in `IDocumentSession` or has
some dependency that itself depends on `IDocumentSession`) as long as the `IntegrateWithWolverine()` call was used in application bootstrapping.

In the previous section we saw an example of incorporating Wolverine's outbox with Marten transactions. We also wrote a fair amount of code to do so that could easily feel
repetitive over time. Using Wolverine's transactional middleware support for Marten, the long hand handler above can become this equivalent:

<!-- snippet: sample_shorthand_order_handler -->
<a id='snippet-sample_shorthand_order_handler'></a>
```cs
// Note that we're able to avoid doing any kind of asynchronous
// code in this handler
[Transactional]
public static OrderCreated Handle(CreateOrder command, IDocumentSession session)
{
    var order = new Order
    {
        Description = command.Description
    };

    // Register the new document with Marten
    session.Store(order);

    // Utilizing Wolverine's "cascading messages" functionality
    // to have this message sent through Wolverine
    return new OrderCreated(order.Id);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WebApiWithMarten/Order.cs#L51-L71' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_shorthand_order_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or if you need to take more control over how the outgoing `OrderCreated` message is sent, you can use this slightly different alternative:

<!-- snippet: sample_shorthand_order_handler_alternative -->
<a id='snippet-sample_shorthand_order_handler_alternative'></a>
```cs
[Transactional]
public static ValueTask Handle(
    CreateOrder command,
    IDocumentSession session,
    IMessageBus bus)
{
    var order = new Order
    {
        Description = command.Description
    };

    // Register the new document with Marten
    session.Store(order);

    // Utilizing Wolverine's "cascading messages" functionality
    // to have this message sent through Wolverine
    return bus.SendAsync(
        new OrderCreated(order.Id),
        new DeliveryOptions { DeliverWithin = 5.Minutes() });
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WebApiWithMarten/Order.cs#L76-L99' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_shorthand_order_handler_alternative' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In both cases Wolverine's transactional middleware for Marten is taking care of registering the Marten session with Wolverine's outbox before you call into the message handler, and
also calling Marten's `IDocumentSession.SaveChangesAsync()` afterward. Used judiciously, this might allow you to avoid more messy or noisy asynchronous code in your
application handler code.

::: tip
This [Transactional] attribute can appear on either the handler class that will apply to all the actions on that class, or on a specific action method.
:::

If so desired, you *can* also use a policy to apply the Marten transaction semantics with a policy. As an example, let's say that you want every message handler where the message type
name ends with "Command" to use the Marten transaction middleware. You could accomplish that
with a handler policy like this:

<!-- snippet: sample_CommandsAreTransactional -->
<a id='snippet-sample_commandsaretransactional'></a>
```cs
public class CommandsAreTransactional : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IContainer container)
    {
        // Important! Create a brand new TransactionalFrame
        // for each chain
        chains
            .Where(chain => chain.MessageType.Name.EndsWith("Command"))
            .Each(chain => chain.Middleware.Add(new TransactionalFrame(chain)));
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Marten/transactional_frame_end_to_end.cs#L84-L98' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_commandsaretransactional' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Then add the policy to your application like this:

<!-- snippet: sample_Using_CommandsAreTransactional -->
<a id='snippet-sample_using_commandsaretransactional'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // And actually use the policy
        opts.Policies.Add<CommandsAreTransactional>();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Marten/transactional_frame_end_to_end.cs#L44-L53' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_commandsaretransactional' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->



## Marten as Inbox

On the flip side of using Wolverine's "outbox" support for outgoing messages, you can also choose to use the same message persistence for incoming messages such that
incoming messages are first persisted to the application's underlying Postgresql database before being processed. While
you *could* use this with external message brokers like Rabbit MQ, it's more likely this will be valuable for Wolverine's [local queues](/guide/messaging/transports/local).

Back to the sample Marten + Wolverine integration from this page:

<!-- snippet: sample_integrating_wolverine_with_marten -->
<a id='snippet-sample_integrating_wolverine_with_marten'></a>
```cs
var builder = WebApplication.CreateBuilder(args);
builder.Host.ApplyOaktonExtensions();

builder.Services.AddMarten(opts =>
    {
        var connectionString = builder
            .Configuration
            .GetConnectionString("postgres");

        opts.Connection(connectionString);
        opts.DatabaseSchemaName = "orders";
    })
    // Optionally add Marten/Postgresql integration
    // with Wolverine's outbox
    .IntegrateWithWolverine();

// You can also place the Wolverine database objects
// into a different database schema, in this case
// named "wolverine_messages"
//.IntegrateWithWolverine("wolverine_messages");

builder.Host.UseWolverine(opts =>
{
    // I've added persistent inbox
    // behavior to the "important"
    // local queue
    opts.LocalQueue("important")
        .UseDurableInbox();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WebApiWithMarten/Program.cs#L8-L40' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_integrating_wolverine_with_marten' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

But this time, focus on the Wolverine configuration of the local queue named "important." By marking this local queue as persistent, any messages sent to this queue
in memory are first persisted to the underlying Postgresql database, and deleted when the message is successfully processed. This allows Wolverine to grant a stronger
delivery guarantee to local messages and even allow messages to be processed if the current application node fails before the message is processed.

::: tip
There are some vague plans to add a little more efficient integration between Wolverine and ASP.Net Core Minimal API, but we're not there yet.
:::

Or finally, it's less code to opt into Wolverine's outbox by delegating to the [command bus](/guide/in-memory-bus) functionality as in this sample [Minimal API](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis?view=aspnetcore-6.0) usage:

<!-- snippet: sample_delegate_to_command_bus_from_minimal_api -->
<a id='snippet-sample_delegate_to_command_bus_from_minimal_api'></a>
```cs
// Delegate directly to Wolverine commands -- More efficient recipe coming later...
app.MapPost("/orders/create2", (CreateOrder command, IMessageBus bus)
    => bus.InvokeAsync(command));
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WebApiWithMarten/Program.cs#L53-L59' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_delegate_to_command_bus_from_minimal_api' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Saga Storage

Marten is an easy option for [persistent sagas](/guide/durability/sagas) with Wolverine. Yet again, to opt into using Marten as your saga storage mechanism in Wolverine, you
just need to add the `IntegrateWithWolverine()` option to your Marten configuration as shown in the [Getting Started](#getting-started) section above.

When using the Wolverine + Marten integration, your stateful saga classes should be valid Marten document types that inherit from Wolverine's `Saga` type, which generally means being a public class with a valid
Marten [identity member](https://martendb.io/documents/identity.html). Remember that your handler methods in Wolverine can accept "method injected" dependencies from your underlying
IoC container.

See the [Saga with Marten sample project](https://github.com/JasperFx/wolverine/tree/main/src/Samples/OrderSagaSample).

## Event Store & CQRS Support






