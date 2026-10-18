using FluentValidation;
using Marten;
using Wolverine.Http;

namespace Ordering;

public record CreateOrder(
    Guid CustomerId,
    string OrderName,
    string FirstName,
    string LastName,
    string EmailAddress,
    string AddressLine,
    string Country,
    string State,
    string ZipCode,
    string CardName,
    string CardNumber,
    string Expiration,
    string CVV,
    int PaymentMethod,
    List<OrderItem> Items
)
{
    public class Validator : AbstractValidator<CreateOrder>
    {
        public Validator()
        {
            RuleFor(x => x.OrderName).NotEmpty();
            RuleFor(x => x.CustomerId).NotEmpty();
            RuleFor(x => x.Items).NotEmpty();
        }
    }
}

public static class CreateOrderEndpoint
{
    [WolverinePost("/orders")]
    public static Order Post(CreateOrder command, IDocumentSession session)
    {
        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = command.CustomerId,
            OrderName = command.OrderName,
            FirstName = command.FirstName,
            LastName = command.LastName,
            EmailAddress = command.EmailAddress,
            AddressLine = command.AddressLine,
            Country = command.Country,
            State = command.State,
            ZipCode = command.ZipCode,
            CardName = command.CardName,
            CardNumber = command.CardNumber,
            Expiration = command.Expiration,
            CVV = command.CVV,
            PaymentMethod = command.PaymentMethod,
            Status = OrderStatus.Pending,
            Items = command.Items,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        session.Store(order);
        return order;
    }
}
