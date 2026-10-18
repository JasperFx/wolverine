using Marten;
using Shared;

namespace Ordering;

/// <summary>
/// Handles BasketCheckoutEvent from the Basket service.
/// Replaces MassTransit IConsumer + MediatR CreateOrderCommand roundtrip.
/// The original had: MassTransit consumer → MediatR Send → CreateOrderHandler → EF Core.
/// Now it's a single Wolverine handler that creates the Order directly.
/// </summary>
public static class BasketCheckoutEventHandler
{
    public static void Handle(BasketCheckoutEvent message, IDocumentSession session)
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = message.CustomerId,
            OrderName = $"Order-{message.UserName}-{DateTime.UtcNow.Ticks}",
            FirstName = message.FirstName,
            LastName = message.LastName,
            EmailAddress = message.EmailAddress,
            AddressLine = message.AddressLine,
            Country = message.Country,
            State = message.State,
            ZipCode = message.ZipCode,
            CardName = message.CardName,
            CardNumber = message.CardNumber,
            Expiration = message.Expiration,
            CVV = message.CVV,
            PaymentMethod = message.PaymentMethod,
            Status = OrderStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        session.Store(order);

        Console.WriteLine($"[OrderCreated] Order {order.Id} created for customer {message.CustomerId} (total: {message.TotalPrice:C})");
    }
}
