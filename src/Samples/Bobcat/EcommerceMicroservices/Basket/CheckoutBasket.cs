using Marten;
using Microsoft.AspNetCore.Mvc;
using Shared;
using Wolverine.Http;
using Wolverine.Persistence;

namespace Basket;

public record CheckoutBasket(
    string UserName,
    Guid CustomerId,
    // Shipping
    string FirstName,
    string LastName,
    string EmailAddress,
    string AddressLine,
    string Country,
    string State,
    string ZipCode,
    // Payment
    string CardName,
    string CardNumber,
    string Expiration,
    string CVV,
    int PaymentMethod
);

public static class CheckoutBasketEndpoint
{
    public static async Task<ProblemDetails> ValidateAsync(
        CheckoutBasket command,
        IQuerySession session)
    {
        var cart = await session.LoadAsync<ShoppingCart>(command.UserName);
        if (cart is null || cart.Items.Count == 0)
            return new ProblemDetails
            {
                Detail = $"Basket not found for user '{command.UserName}'",
                Status = 404,
            };

        return WolverineContinue.NoProblems;
    }

    // Cascade: first element is HTTP response (bool), second is the integration event
    // published via Wolverine outbox to be consumed by the Ordering service.
    // After publishing, delete the basket.
    [WolverinePost("/basket/checkout")]
    public static async Task<(bool, BasketCheckoutEvent)> Post(
        CheckoutBasket command,
        IDocumentSession session,
        CancellationToken ct)
    {
        var cart = await session.LoadAsync<ShoppingCart>(command.UserName, ct);

        var checkoutEvent = new BasketCheckoutEvent(
            command.UserName,
            command.CustomerId,
            cart!.TotalPrice,
            command.FirstName,
            command.LastName,
            command.EmailAddress,
            command.AddressLine,
            command.Country,
            command.State,
            command.ZipCode,
            command.CardName,
            command.CardNumber,
            command.Expiration,
            command.CVV,
            command.PaymentMethod
        );

        session.Delete(cart);

        return (true, checkoutEvent);
    }
}
