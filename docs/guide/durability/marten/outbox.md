# Marten as Transactional Outbox

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
<a id='snippet-sample_CreateOrderController'></a>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/WebApiWithMarten/Order.cs#L20-L47' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_CreateOrderController' title='Start of snippet'>anchor</a></sup>
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

