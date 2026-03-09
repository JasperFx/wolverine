# Polecat as Transactional Outbox

::: tip
Wolverine's outbox will help you order all outgoing messages until after the database transaction succeeds, but only messages being delivered
to endpoints explicitly configured to be persistent will be stored in the database.
:::

One of the most important features in all of Wolverine is the [persistent outbox](https://microservices.io/patterns/data/transactional-outbox.html) support and its easy integration into Polecat.

Here's a common problem when using any kind of messaging strategy. Inside the handling for a single web request, you need to make some immediate writes to
the backing database for the application, then send a corresponding message out through your asynchronous messaging infrastructure. Easy enough, but here's a few ways
that could go wrong:

* The message is received and processed before the initial database writes are committed
* The database transaction fails, but the message was still sent out
* The database transaction succeeds, but the message infrastructure fails

This is where the "outbox" pattern comes into play to guarantee
that the outgoing message and database transaction both succeed or fail, and that the message is only sent out after the database transaction has succeeded.

Imagine a simple example where a Wolverine handler is receiving a `CreateOrder` command that will create a new Polecat `Order` document and also publish
an `OrderCreated` event through Wolverine messaging. Using the outbox, that handler **in explicit, long hand form** is this:

```cs
public static async Task Handle(
    CreateOrder command,
    IDocumentSession session,
    IMessageBus bus,
    CancellationToken cancellation)
{
    var order = new Order
    {
        Description = command.Description
    };

    // Register the new document with Polecat
    session.Store(order);

    // Hold on though, this message isn't actually sent
    // until the Polecat session is committed
    await bus.SendAsync(new OrderCreated(order.Id));

    // This makes the database commits, *then* flushed the
    // previously registered messages to Wolverine's sending
    // agents
    await session.SaveChangesAsync(cancellation);
}
```

When `IDocumentSession.SaveChangesAsync()` is called, Polecat is persisting the new `Order` document **and** creating database records for the outgoing `OrderCreated` message
in the same transaction. After the database transaction succeeds, the pending messages are automatically sent to Wolverine's sending agents.

Now, let's play "what if:"

* What if the messaging broker is down? As long as the messages are persisted, Wolverine will continue trying to send the persisted outgoing messages until the messaging broker is back up.
* What if the application dies after the database transaction but before the messages are sent? Wolverine will still be able to send these persisted messages from either another running application node or after the application is restarted.

In the section below on transactional middleware we'll see a shorthand way to simplify the code sample above.

## Outbox with ASP.Net Core

The Wolverine outbox is also usable from within ASP.Net Core controller or Minimal API handler code. Within an MVC controller:

```cs
public class CreateOrderController : ControllerBase
{
    [HttpPost("/orders/create2")]
    public async Task Create(
        [FromBody] CreateOrder command,
        [FromServices] IDocumentSession session,
        [FromServices] IMessageBus bus)
    {
        var order = new Order
        {
            Description = command.Description
        };

        // Register the new document with Polecat
        session.Store(order);

        // Don't worry, this message doesn't go out until
        // after the Polecat transaction succeeds
        await bus.PublishAsync(new OrderCreated(order.Id));

        // Commit the Polecat transaction
        await session.SaveChangesAsync();
    }
}
```

From a Minimal API:

```cs
app.MapPost("/orders/create3", async (CreateOrder command, IDocumentSession session, IMessageBus bus) =>
{
    var order = new Order
    {
        Description = command.Description
    };

    session.Store(order);

    await bus.PublishAsync(new OrderCreated(order.Id));

    await session.SaveChangesAsync();
});
```
