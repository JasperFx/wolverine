using Marten;
using Marten.Events;
using Marten.Events.Aggregation;
using Messages;
using Microsoft.Extensions.Logging;

namespace Orders;

public record PurchaseOrder(string Id, string Status = "Placed");

public class PurchaseOrderProjection : SingleStreamProjection<PurchaseOrder>
{
    public static PurchaseOrder Create(
        IEvent<OrderPlaced> @event
    )
    {
        return new PurchaseOrder(@event.Data.OrderId);
    }

    public static PurchaseOrder Apply(
        IEvent<OrderRejected> @event,
        PurchaseOrder current
    )
    {
        return current with { Status = "Rejected" };
    }

    public static PurchaseOrder Apply(
        IEvent<OrderApproved> @event,
        PurchaseOrder current
    )
    {
        return current with { Status = "Approved" };
    }
}

public class OrderPlacedHandler
{
    public async Task<object[]> Handle(
        PlaceOrder message,
        IDocumentSession session,
        ILogger logger
    )
    {
        var (orderId, customerId, amount) = message;
        logger.LogInformation("Received Order: {OrderId}", message.OrderId);
        var orderPlaced = new OrderPlaced(
            orderId,
            customerId,
            amount
        );
        session.Events.Append(orderId, orderPlaced);
        await session.SaveChangesAsync();

        var order = await session.LoadAsync<PurchaseOrder>(orderId);

        return [order, orderPlaced];
    }
}