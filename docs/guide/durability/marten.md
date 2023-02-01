# Marten Integration

[Marten](https://martendb.io) and Wolverine are sibling projects under the [JasperFx organization](https://github.com/wolverinefx), and as such, have quite a bit of synergy when
used together. At this point, adding the `WolverineFx.Marten`*` Nuget dependency to your application adds the capability to combine Marten and Wolverine to:

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WebApiWithMarten/Program.cs#L57-L77' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_create_order_through_minimal_api' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->



## Transactional Middleware

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
        opts.Handlers.AutoApplyTransactions();
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
also calling Marten's `IDocumentSession.SaveChangesAsync()` afterward. Used judiciously, this might allow you to avoid more messy or noising asynchronous code in your
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
    public void Apply(HandlerGraph graph, GenerationRules rules, IContainer container)
    {
        // Important! Create a brand new TransactionalFrame
        // for each chain
        graph
            .Chains
            .Where(x => x.MessageType.Name.EndsWith("Command"))
            .Each(x => x.Middleware.Add(new TransactionalFrame()));
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Marten/transactional_frame_end_to_end.cs#L87-L102' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_commandsaretransactional' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Then add the policy to your application like this:

<!-- snippet: sample_Using_CommandsAreTransactional -->
<a id='snippet-sample_using_commandsaretransactional'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // And actually use the policy
        opts.Handlers.AddPolicy<CommandsAreTransactional>();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Marten/transactional_frame_end_to_end.cs#L47-L56' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_commandsaretransactional' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WebApiWithMarten/Program.cs#L48-L54' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_delegate_to_command_bus_from_minimal_api' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Saga Storage

Marten is an easy option for [persistent sagas](/guide/durability/sagas) with Wolverine. Yet again, to opt into using Marten as your saga storage mechanism in Wolverine, you
just need to add the `IntegrateWithWolverine()` option to your Marten configuration as shown in the [Getting Started](#getting-started) section above.

When using the Wolverine + Marten integration, your stateful saga classes should be valid Marten document types that inherit from Wolverine's `Saga` type, which generally means being a public class with a valid
Marten [identity member](https://martendb.io/documents/identity.html). Remember that your handler methods in Wolverine can accept "method injected" dependencies from your underlying
IoC container.

See the [Saga with Marten sample project](https://github.com/JasperFx/wolverine/tree/main/src/Samples/OrderSagaSample).

## Event Store & CQRS Support

::: tip
You can forgo the `[MartenCommandWorkflow]` attribute by instead naming your message handler type with the `AggregateHandler` suffix
if the Wolverine/Marten integration is applied to your application.
:::

See the [OrderEventSourcingSample project on GitHub](https://github.com/JasperFx/wolverine/tree/main/src/Samples/OrderEventSourcingSample) for more samples.

That Wolverine + Marten combination is optimized for efficient and productive development using a [CQRS architecture style](https://martinfowler.com/bliki/CQRS.html) with [Marten's event sourcing](https://martendb.io/events/) support.
Specifically, let's dive into the responsibilities of a typical command handler in a CQRS with event sourcing architecture:

1. Fetch any current state of the system that's necessary to evaluate or validate the incoming event
2. *Decide* what events should be emitted and captured in response to an incoming event
3. Manage concurrent access to system state
4. Safely commit the new events
5. Selectively publish some of the events based on system needs to other parts of your system or even external systems
6. Instrument all of the above

And then lastly, you're going to want some resiliency and selective retry capabilities for concurrent access violations or just normal infrastructure hiccups.

Let's just right into an example order management system. I'm going to model the order workflow with this aggregate model:

<!-- snippet: sample_Order_event_sourced_aggregate -->
<a id='snippet-sample_order_event_sourced_aggregate'></a>
```cs
public class Item
{
    public string Name { get; set; }
    public bool Ready { get; set; }
}

public class Order
{
    public Order(OrderCreated created)
    {
        foreach (var item in created.Items) Items[item.Name] = item;
    }

    // This would be the stream id
    public Guid Id { get; set; }

    // This is important, by Marten convention this would
    // be the
    public int Version { get; set; }

    public DateTimeOffset? Shipped { get; private set; }

    public Dictionary<string, Item> Items { get; set; } = new();

    // These methods are used by Marten to update the aggregate
    // from the raw events
    public void Apply(IEvent<OrderShipped> shipped)
    {
        Shipped = shipped.Timestamp;
    }

    public void Apply(ItemReady ready)
    {
        Items[ready.Name].Ready = true;
    }

    public bool IsReadyToShip()
    {
        return Shipped == null && Items.Values.All(x => x.Ready);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/OrderEventSourcingSample/Order.cs#L18-L62' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_order_event_sourced_aggregate' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

At a minimum, we're going to want a command handler for this command message that marks an order item as ready to ship and then evaluates whether
or not based on the current state of the `Order` aggregate whether or not the logical order is ready to be shipped out:

<!-- snippet: sample_MarkItemReady -->
<a id='snippet-sample_markitemready'></a>
```cs
// OrderId refers to the identity of the Order aggregate
public record MarkItemReady(Guid OrderId, string ItemName, int Version);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/OrderEventSourcingSample/Order.cs#L64-L69' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_markitemready' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the code above we're also utilizing Wolverine's [outbox messaging](/guide/durability/) support to both order and guarantee the delivery of a `ShipOrder` message when
the Marten transaction

Before getting into Wolverine middleware strategies, let's first build out an MVC controller method for the command above:

<!-- snippet: sample_MarkItemController -->
<a id='snippet-sample_markitemcontroller'></a>
```cs
[HttpPost("/orders/itemready")]
public async Task Post(
    [FromBody] MarkItemReady command,
    [FromServices] IDocumentSession session,
    [FromServices] IMartenOutbox outbox
)
{
    // Fetch the current value of the Order aggregate
    var stream = await session
        .Events

        // We're also opting into Marten optimistic concurrency checks here
        .FetchForWriting<Order>(command.OrderId, command.Version);

    var order = stream.Aggregate;

    if (order.Items.TryGetValue(command.ItemName, out var item))
    {
        item.Ready = true;

        // Mark that the this item is ready
        stream.AppendOne(new ItemReady(command.ItemName));
    }
    else
    {
        // Some crude validation
        throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
    }

    // If the order is ready to ship, also emit an OrderReady event
    if (order.IsReadyToShip())
    {
        // Publish a cascading command to do whatever it takes
        // to actually ship the order
        // Note that because the context here is enrolled in a Wolverine
        // outbox, the message is registered, but not "released" to
        // be sent out until SaveChangesAsync() is called down below
        await outbox.PublishAsync(new ShipOrder(command.OrderId));
        stream.AppendOne(new OrderReady());
    }

    // This will also persist and flush out any outgoing messages
    // registered into the context outbox
    await session.SaveChangesAsync();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/OrderEventSourcingSample/Order.cs#L73-L121' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_markitemcontroller' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Hopefully that code is easy to understand, but there's some potentially repetitive code
(loading aggregates, appending events, committing transactions) that's going to reoccur
across basically all your command handlers. Likewise, it would be best if you could completely
isolate your business logic that *decides* what new events should be appended completely
away from the infrastructure code so that you can more easily reason about that code and
easily test that business logic. To that end, Wolverine supports the [Decider](https://thinkbeforecoding.com/post/2021/12/17/functional-event-sourcing-decider)
pattern with Marten using the `[MartenCommandWorkflow]` middleware.
Using that middleware, we get this slimmer code:

<!-- snippet: sample_MarkItemReadyHandler -->
<a id='snippet-sample_markitemreadyhandler'></a>
```cs
[MartenCommandWorkflow]
public static IEnumerable<object> Handle(MarkItemReady command, Order order)
{
    if (order.Items.TryGetValue(command.ItemName, out var item))
    {
        // Not doing this in a purist way here, but just
        // trying to illustrate the Wolverine mechanics
        item.Ready = true;

        // Mark that the this item is ready
        yield return new ItemReady(command.ItemName);
    }
    else
    {
        // Some crude validation
        throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
    }

    // If the order is ready to ship, also emit an OrderReady event
    if (order.IsReadyToShip())
    {
        yield return new OrderReady();
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/OrderEventSourcingSample/Order.cs#L248-L275' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_markitemreadyhandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the case above, Wolverine is wrapping middleware around our basic command handler to
to:

1. Fetch the appropriate `Order` aggregate matching the command
2. Append any new events returned from the handle method to the Marten event stream
   for this `Order`
3. Saves any outstanding changes and commits the Marten unit of work

To make this more clear, here's the generated code (with some reformatting and extra comments):

<!-- snippet: sample_generated_MarkItemReadyHandler -->
<a id='snippet-sample_generated_markitemreadyhandler'></a>
```cs
public class MarkItemReadyHandler1442193977 : MessageHandler
{
    private readonly OutboxedSessionFactory _outboxedSessionFactory;

    public MarkItemReadyHandler1442193977(OutboxedSessionFactory outboxedSessionFactory)
    {
        _outboxedSessionFactory = outboxedSessionFactory;
    }

    public override async Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        var markItemReady = (MarkItemReady)context.Envelope.Message;
        await using var documentSession = _outboxedSessionFactory.OpenSession(context);
        var eventStore = documentSession.Events;
        // Loading Marten aggregate
        var eventStream = await eventStore.FetchForWriting<Order>(markItemReady.OrderId, markItemReady.Version, cancellation).ConfigureAwait(false);

        var outgoing1 = MarkItemReadyHandler.Handle(markItemReady, eventStream.Aggregate);
        if (outgoing1 != null)
        {
            // Capturing any possible events returned from the command handlers
            eventStream.AppendMany(outgoing1);

        }

        await documentSession.SaveChangesAsync(cancellation).ConfigureAwait(false);
    }

}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/OrderEventSourcingSample/Internal/Generated/JasperHandlers/MarkItemReadyHandler1442193977.cs.cs#L13-L45' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_generated_markitemreadyhandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

As you probably guessed, there are some naming conventions or other questions you need to be aware of
before you use this middleware strategy.

### Handler Method Signatures

The Marten workflow command handler method signature needs to follow these rules:

* Either explicitly use the `[MartenCommandWorkflow]` attribute on the handler method **or use the `AggregateHandler` suffix** on the message handler type to tell Wolverine to opt into the aggregate command workflow.
* The first argument should be the command type, just like any other Wolverine message handler
* The 2nd argument should be the aggregate -- either the aggregate itself (`Order`) or wrapped
  in the Marten `IEventStream<T>` type (`IEventStream<Order>`). There is an example of that usage below:

<!-- snippet: sample_MarkItemReadyHandler_with_explicit_stream -->
<a id='snippet-sample_markitemreadyhandler_with_explicit_stream'></a>
```cs
[MartenCommandWorkflow]
public static void Handle(OrderEventSourcingSample.MarkItemReady command, IEventStream<Order> stream)
{
    var order = stream.Aggregate;

    if (order.Items.TryGetValue(command.ItemName, out var item))
    {
        // Not doing this in a purist way here, but just
        // trying to illustrate the Wolverine mechanics
        item.Ready = true;

        // Mark that the this item is ready
        stream.AppendOne(new ItemReady(command.ItemName));
    }
    else
    {
        // Some crude validation
        throw new InvalidOperationException($"Item {command.ItemName} does not exist in this order");
    }

    // If the order is ready to ship, also emit an OrderReady event
    if (order.IsReadyToShip())
    {
        stream.AppendOne(new OrderReady());
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/OrderEventSourcingSample/Alternatives/Signatures.cs#L25-L54' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_markitemreadyhandler_with_explicit_stream' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Just as in other Wolverine [message handlers](/guide/handlers/), you can use
additional method arguments for registered services ("method injection"), the `CancellationToken`
for the message, and the message `Envelope` if you need access to message metadata.

### Determining the Aggregate Identity

Wolverine is trying to determine a public member on the command type that refers to the identity
of the aggregate type. You've got two options, either use the implied naming convention below
where the `OrderId` property is assumed to be the identity of the `Order` aggregate
by appending "Id" to the aggregate type name (it's not case sensitive if you were wondering):

<!-- snippet: sample_MarkItemReady -->
<a id='snippet-sample_markitemready'></a>
```cs
// OrderId refers to the identity of the Order aggregate
public record MarkItemReady(Guid OrderId, string ItemName, int Version);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/OrderEventSourcingSample/Order.cs#L64-L69' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_markitemready' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or if you want to use a different member, bypass the convention, or just don't like conventional
magic, you can decorate a public member
on the command class with Marten's `[Identity]` attribute like so:

<!-- snippet: sample_MarkItemReady_with_explicit_identity -->
<a id='snippet-sample_markitemready_with_explicit_identity'></a>
```cs
public class MarkItemReady
{
    // This attribute tells Wolverine that this property will refer to the
    // Order aggregate
    [Identity] public Guid Id { get; init; }

    public string ItemName { get; init; }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/OrderEventSourcingSample/Alternatives/Signatures.cs#L8-L19' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_markitemready_with_explicit_identity' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Publishing Events

::: tip
This functionality is *brand spanking new* and will likely be enhanced after user feedback.
:::

You can also opt to automatically publish any event types captured by Marten through Wolverine's persistent
outbox. Do note that only event types that have a matching subscription in the Wolverine configuration
will actually be published.

To opt into this feature, chain the Wolverine `AddMarten().EventForwardingToWolverine()` call as
shown in this application bootstrapping sample shown below:

<!-- snippet: sample_opting_into_wolverine_event_publishing -->
<a id='snippet-sample_opting_into_wolverine_event_publishing'></a>
```cs
builder.Services.AddMarten(opts =>
    {
        var connString = builder
            .Configuration
            .GetConnectionString("marten");

        opts.Connection(connString);

        // There will be more here later...

        opts.Projections
            .Add<AppointmentDurationProjection>(ProjectionLifecycle.Async);

        // OR ???

        opts.Projections
            .Add<AppointmentDurationProjection>(ProjectionLifecycle.Inline);

        opts.Projections.Add<AppointmentProjection>(ProjectionLifecycle.Inline);
        opts.Projections
            .SelfAggregate<ProviderShift>(ProjectionLifecycle.Async);
    })

    // This adds a hosted service to run
    // asynchronous projections in a background process
    .AddAsyncDaemon(DaemonMode.HotCold)

    // I added this to enroll Marten in the Wolverine outbox
    .IntegrateWithWolverine()

    // I also added this to opt into events being forward to
    // the Wolverine outbox during SaveChangesAsync()
    .EventForwardingToWolverine();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/CQRSWithMarten/TeleHealth.WebApi/Program.cs#L45-L81' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_opting_into_wolverine_event_publishing' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

This does need to be paired with a little bit of Wolverine configuration to add
subscriptions to event types like so:

<!-- snippet: sample_configuring_wolverine_event_subscriptions -->
<a id='snippet-sample_configuring_wolverine_event_subscriptions'></a>
```cs
builder.Host.UseWolverine(opts =>
{
    // I'm choosing to process any ChartingFinished event messages
    // in a separate, local queue with persistent messages for the inbox/outbox
    opts.PublishMessage<ChartingFinished>()
        .ToLocalQueue("charting")
        .UseDurableInbox();

    // If we encounter a concurrency exception, just try it immediately
    // up to 3 times total
    opts.Handlers.OnException<ConcurrencyException>().RetryTimes(3);

    // It's an imperfect world, and sometimes transient connectivity errors
    // to the database happen
    opts.Handlers.OnException<NpgsqlException>()
        .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/CQRSWithMarten/TeleHealth.WebApi/Program.cs#L17-L37' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_wolverine_event_subscriptions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->




