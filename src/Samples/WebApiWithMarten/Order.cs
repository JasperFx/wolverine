using Baseline.Dates;
using Marten;
using Microsoft.AspNetCore.Mvc;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Persistence.Marten;

namespace WebApiWithMarten;

public class Order
{
    public Guid Id { get; set; }
    public string Description { get; set; }
}

public record CreateOrder(string Description);

public record OrderCreated(Guid Id);


#region sample_CreateOrderController

public class CreateOrderController : ControllerBase
{
    [HttpPost("/orders/create2")]
    public async Task Create(
        [FromBody] CreateOrder command,
        [FromServices] IDocumentSession session,
        [FromServices] IMessageContext context)
    {
        // Gotta connection the Marten session into
        // the Wolverine outbox
        await context.EnlistInOutboxAsync(session);

        var order = new Order
        {
            Description = command.Description,
        };

        // Register the new document with Marten
        session.Store(order);

        // Don't worry, this message doesn't go out until
        // after the Marten transaction succeeds
        await context.PublishAsync(new OrderCreated(order.Id));

        // Commit the Marten transaction
        await session.SaveChangesAsync();
    }
}

#endregion

public class OrderHandler
{
    #region sample_shorthand_order_handler

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

    #endregion


}

public class OrderHandler2
{
    #region sample_shorthand_order_handler_alternative

    [Transactional]
    public static ValueTask Handle(
        CreateOrder command,
        IDocumentSession session,
        IMessagePublisher publisher)
    {
        var order = new Order
        {
            Description = command.Description
        };

        // Register the new document with Marten
        session.Store(order);

        // Utilizing Wolverine's "cascading messages" functionality
        // to have this message sent through Wolverine
        return publisher.SendAsync(
            new OrderCreated(order.Id),
            new DeliveryOptions{DeliverWithin = 5.Minutes()});
    }

    #endregion
}

public class LonghandOrderHandler
{
    #region sample_longhand_order_handler

    public static async Task Handle(
        CreateOrder command,
        IDocumentSession session,
        IMessageContext context,
        CancellationToken cancellation)
    {
        // Connect the Marten session to the outbox
        // scoped to this specific command
        await context.EnlistInOutboxAsync(session);

        var order = new Order
        {
            Description = command.Description,
        };

        // Register the new document with Marten
        session.Store(order);

        // Hold on though, this message isn't actually sent
        // until the Marten session is committed
        await context.SendAsync(new OrderCreated(order.Id));

        // This makes the database commits, *then* flushed the
        // previously registered messages to Wolverine's sending
        // agents
        await session.SaveChangesAsync(cancellation);
    }

    #endregion
}
