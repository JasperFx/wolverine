using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
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

    // Cascade: the first tuple element is the HTTP response, the second is the integration
    // event published through the Wolverine outbox for the Ordering module to consume. After
    // publishing, delete the basket.
    //
    // 202 Accepted, not 201 Created: the order does not exist when this returns. It is created
    // by the Ordering module when it handles BasketCheckoutEvent off the durable local queue,
    // which is the whole point of the modular split — so the honest answer is "accepted, look
    // for the result here", with the customer's orders as the Location.
    [WolverinePost("/basket/checkout")]
    public static async Task<(Accepted, BasketCheckoutEvent)> Post(
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

        return (TypedResults.Accepted($"/orders/customer/{command.CustomerId}"), checkoutEvent);
    }
}
